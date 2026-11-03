// RE-USE の Mamba の selective scan（Mamba-1）。1スレッドが1系列の1チャンネルを受け持ち、状態をレジスターに持って系列を順にたどる。
// 状態を全位置ぶんメモリーに書き出す並列 scan より、メモリーの読み書きが状態の次元（16）分の1で済む。
//   Δ_t = softplus(dt_proj(dbl_t[0..DT_RANK)) + bias)
//   h_t = exp(Δ_t A) h_{t-1} + Δ_t B_t u_t
//   y_t = (C_t · h_t + D u_t) · SiLU(z_t)
// Δ は dt_proj（入力 4 つ）をここで求め、行数 × チャンネル数の Δ をメモリーに書き出して読み直す往復を省く。
// 系列は「行 = 系列番号 × SeqStride + 位置 × PosStride」で並んでいるものとする。
// Direction が -1 のときは系列を逆向きにたどる（双方向 Mamba の後ろ向きの枝。反転した系列を前向きに処理するのと同じ結果になる）。

cbuffer Constants : register(b0)
{
    uint Sequences;
    uint Length;
    uint SeqStride;
    uint PosStride;
    uint Channels;
    int Direction;
    uint DblStride;
    uint BOffset;
    uint COffset;
    uint ZStride;
    uint ZOffset;
    uint Pad0;
};

#define STATE 16
#define DT_RANK 4

StructuredBuffer<float> U : register(t0);
StructuredBuffer<float> Dbl : register(t1);
StructuredBuffer<float> Z : register(t2);
StructuredBuffer<float> A : register(t3);
StructuredBuffer<float> DSkip : register(t4);
StructuredBuffer<float> DtWeight : register(t5);
StructuredBuffer<float> DtBias : register(t6);
RWStructuredBuffer<float> Y : register(u0);

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint d = id.x;
    uint s = id.y;
    if (d >= Channels || s >= Sequences)
        return;

    float a[STATE];
    float h[STATE];
    [unroll] for (uint n = 0; n < STATE; n++)
    {
        a[n] = A[d * STATE + n];
        h[n] = 0;
    }
    float skip = DSkip[d];
    float dtWeight[DT_RANK];
    [unroll] for (uint r = 0; r < DT_RANK; r++)
        dtWeight[r] = DtWeight[d * DT_RANK + r];
    float dtBias = DtBias[d];

    for (uint i = 0; i < Length; i++)
    {
        uint pos = Direction > 0 ? i : Length - 1 - i;
        uint row = s * SeqStride + pos * PosStride;
        float u = U[row * Channels + d];
        uint dbl = row * DblStride;
        float dt = 0;
        [unroll] for (uint k = 0; k < DT_RANK; k++)
            dt = mad(Dbl[dbl + k], dtWeight[k], dt);
        dt += dtBias;
        // PyTorch の softplus と同じく、大きい値はそのまま返す（exp の桁あふれを避ける）
        dt = dt > 20 ? dt : log(1 + exp(dt));
        float du = dt * u;
        float y = 0;
        [unroll] for (uint n = 0; n < STATE; n++)
        {
            h[n] = mad(exp(dt * a[n]), h[n], du * Dbl[dbl + BOffset + n]);
            y = mad(h[n], Dbl[dbl + COffset + n], y);
        }
        y = mad(skip, u, y);
        float z = Z[row * ZStride + ZOffset + d];
        Y[row * Channels + d] = y * (z / (1 + exp(-z)));
    }
}

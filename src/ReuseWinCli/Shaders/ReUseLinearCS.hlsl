// RE-USE（音声ノイズ除去）の全結合層。行ごとに Y = X・Wᵀ (+ bias) を求める。
// 入出力は行の幅と列の位置を指定できるので、幅の広いバッファの一部の列を読み書きできる。
// W は PyTorch の nn.Linear と同じ [N][K] の並び。
// CAUSAL_CONV を定義してコンパイルしたもの（ReUseLinearConvCS）は、X を Mamba の深さ方向の因果畳み込み（カーネル幅 4）と SiLU に
// 通しながら読み、畳み込みの結果を ConvOut にも書く（x_proj。畳み込みの結果は selective scan も読む）。
// 分岐で切り替えると、使わない畳み込みの分までレジスターを取り、ほかの全結合層が遅くなるので、別のシェーダーにしている。
// 畳み込みの系列のたどり方は ReUseSelectiveScanCS と同じで、位置は (行 / ConvPosStride) % ConvLength、
// ConvDirection が -1 なら系列を逆向きにたどる。ConvOut を書くのは列のタイルの先頭のグループだけなので、N ≤ TILE のとき無駄がない。

cbuffer Constants : register(b0)
{
    uint M;
    uint N;
    uint K;
    uint Flags;
    uint XStride;
    uint XOffset;
    uint YStride;
    uint YOffset;
    uint RStride;
    uint ROffset;
    uint ConvLength;
    uint ConvPosStride;
    int ConvDirection;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

#define FLAG_BIAS 1
#define FLAG_RESIDUAL 4
#define CONV_KERNEL 4

StructuredBuffer<float> X : register(t0);
StructuredBuffer<float> W : register(t1);
StructuredBuffer<float> Bias : register(t2);
StructuredBuffer<float> Residual : register(t3);
RWStructuredBuffer<float> Y : register(u0);
#ifdef CAUSAL_CONV
StructuredBuffer<float> ConvWeight : register(t4);
StructuredBuffer<float> ConvBias : register(t5);
RWStructuredBuffer<float> ConvOut : register(u1);
#endif

#define TILE 64
#define TILE_K 32
#define TILE4 (TILE / 4)
// 1 スレッドが 1 回に置く float4 の数
#define LOADS (TILE_K * TILE4 / 256)

// スレッドは 4 行 × 4 列を受け持ち、共有メモリーから float4 で読む。
// 行の長さを float4 1 つ分余らせて、書き込むときのバンクの衝突を避ける
groupshared float4 tileX[TILE_K][TILE4 + 1];
groupshared float4 tileW[TILE_K][TILE4 + 1];

[numthreads(16, 16, 1)]
void main(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    // 同じ行の列のタイルを続けて走らせ、X を L2 に残ったまま読む
    uint rowBase = group.y * TILE;
    uint colBase = group.x * TILE;
    uint local = thread.y * 16 + thread.x;
    // 隣り合うスレッドが隣り合う k を読み（グローバルメモリーの読み込みをまとめるため）、4 行（4 列）分を float4 にして置く
    uint loadK = local % TILE_K;
    uint loadQ = local / TILE_K;
#ifdef CAUSAL_CONV
    bool writeConv = group.x == 0;
    // 読み込みで受け持つ行の、畳み込みの系列の中の位置
    int convPos[LOADS * 4];
    [unroll] for (uint lr = 0; lr < LOADS * 4; lr++)
    {
        uint row = rowBase + (loadQ + (lr / 4) * (256 / TILE_K)) * 4 + lr % 4;
        convPos[lr] = (int)((row / ConvPosStride) % ConvLength);
    }
#endif

    float4 acc[4];
    [unroll] for (uint i0 = 0; i0 < 4; i0++)
        acc[i0] = 0;

    for (uint k0 = 0; k0 < K; k0 += TILE_K)
    {
        uint k = k0 + loadK;
        [unroll] for (uint l = 0; l < LOADS; l++)
        {
            uint q = loadQ + l * (256 / TILE_K);
            float4 xv;
            float4 wv;
            [unroll] for (uint c = 0; c < 4; c++)
            {
                uint row = rowBase + q * 4 + c;
                uint col = colBase + q + 16 * c;
                bool inside = row < M && k < K;
                float x = 0;
#ifdef CAUSAL_CONV
                if (inside)
                {
                    int pos = convPos[l * 4 + c];
                    x = ConvBias[k];
                    [unroll] for (int j = 0; j < CONV_KERNEL; j++)
                    {
                        int back = (CONV_KERNEL - 1 - j) * ConvDirection;
                        int p = pos - back;
                        if (p >= 0 && p < (int)ConvLength)
                        {
                            int r = (int)row - back * (int)ConvPosStride;
                            x = mad(ConvWeight[k * CONV_KERNEL + j], X[r * XStride + XOffset + k], x);
                        }
                    }
                    x = x / (1 + exp(-x));
                    if (writeConv)
                        ConvOut[row * XStride + XOffset + k] = x;
                }
#else
                if (inside)
                    x = X[row * XStride + XOffset + k];
#endif
                xv[c] = x;
                wv[c] = (col < N && k < K) ? W[col * K + k] : 0;
            }
            tileX[loadK][q] = xv;
            tileW[loadK][q] = wv;
        }
        GroupMemoryBarrierWithGroupSync();

        [unroll] for (uint kk = 0; kk < TILE_K; kk++)
        {
            float4 a = tileX[kk][thread.y];
            float4 b = tileW[kk][thread.x];
            acc[0] = mad(a.x, b, acc[0]);
            acc[1] = mad(a.y, b, acc[1]);
            acc[2] = mad(a.z, b, acc[2]);
            acc[3] = mad(a.w, b, acc[3]);
        }
        GroupMemoryBarrierWithGroupSync();
    }

    [unroll] for (uint i = 0; i < 4; i++)
    {
        uint row = rowBase + thread.y * 4 + i;
        if (row >= M)
            continue;
        [unroll] for (uint j = 0; j < 4; j++)
        {
            uint col = colBase + thread.x + 16 * j;
            if (col >= N)
                continue;
            float v = acc[i][j];
            if (Flags & FLAG_BIAS)
                v += Bias[col];
            if (Flags & FLAG_RESIDUAL)
                v += Residual[row * RStride + ROffset + col];
            Y[row * YStride + YOffset + col] = v;
        }
    }
}

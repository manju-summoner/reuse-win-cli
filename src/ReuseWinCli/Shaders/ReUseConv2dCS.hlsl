// RE-USE のエンコーダー・デコーダーの 2 次元畳み込み。im2col を作らずに、行列積のタイルへ読み込むときに入力の位置を求める（implicit GEMM）。
// 活性は [高さ（時間）][幅（周波数）][チャンネル] の並び。重みは [出力][カーネルの行][カーネルの列][入力] に並べ替えて渡す
// （入力チャンネルが最も内側なので、連続した k が連続したアドレスになる）。
// Shuffle は SPConvTranspose2d（出力チャンネルを R 個に分けて、幅か高さを R 倍に並べ直す）を出力の書き込みで行う。
// 畳み込みは入力チャンネルについて線形なので、DenseBlock の連結した入力は、64 チャンネルずつ別々のバッファのまま
// FLAG_ACCUMULATE で足し込んで求める（連結したバッファは D3D11 のリソースの大きさの上限を超えうるため）。

cbuffer Constants : register(b0)
{
    uint InH;
    uint InW;
    uint InStride;
    uint InOffset;
    uint Cin;
    uint OutH;
    uint OutW;
    uint Cout;
    uint KH;
    uint KW;
    uint StrideH;
    uint StrideW;
    uint DilH;
    uint DilW;
    uint PadH;
    uint PadW;
    uint OutStride;
    uint OutOffset;
    uint Shuffle;
    uint R;
    uint StoreW;
    uint Flags;
    uint Pad0;
    uint Pad1;
};

#define SHUFFLE_NONE 0
#define SHUFFLE_H 1
#define SHUFFLE_W 2

#define FLAG_BIAS 1
#define FLAG_ACCUMULATE 2

StructuredBuffer<float> X : register(t0);
StructuredBuffer<float> W : register(t1);
StructuredBuffer<float> Bias : register(t2);
RWStructuredBuffer<float> Y : register(u0);

#define TILE 64
#define TILE_K 32
#define TILE4 (TILE / 4)
// 1 スレッドが 1 回に置く float4 の数
#define LOADS (TILE_K * TILE4 / 256)

// スレッドは 4 行 × 4 列を受け持ち、共有メモリーから float4 で読む（ReUseLinearCS と同じ形）。
// 行の長さを float4 1 つ分余らせて、書き込むときのバンクの衝突を避ける
groupshared float4 tileX[TILE_K][TILE4 + 1];
groupshared float4 tileW[TILE_K][TILE4 + 1];

[numthreads(16, 16, 1)]
void main(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    uint M = OutH * OutW;
    uint K = KH * KW * Cin;
    // 同じ位置の出力チャンネルのタイルを続けて走らせ、入力を L2 に残ったまま読む
    uint rowBase = group.y * TILE;
    uint colBase = group.x * TILE;
    uint local = thread.y * 16 + thread.x;
    // 隣り合うスレッドが隣り合う k を読み（グローバルメモリーの読み込みをまとめるため）、4 行（4 列）分を float4 にして置く
    uint loadK = local % TILE_K;
    uint loadQ = local / TILE_K;

    // 読み込みで受け持つ行の、カーネルの左上に当たる入力の位置。範囲外の行は高さを大きな負にして読まない
    int baseH[LOADS * 4];
    int baseW[LOADS * 4];
    [unroll] for (uint lr = 0; lr < LOADS * 4; lr++)
    {
        uint row = rowBase + (loadQ + (lr / 4) * (256 / TILE_K)) * 4 + lr % 4;
        baseH[lr] = row < M ? (int)((row / OutW) * StrideH) - (int)PadH : -0x40000000;
        baseW[lr] = (int)((row % OutW) * StrideW) - (int)PadW;
    }

    float4 acc[4];
    [unroll] for (uint i0 = 0; i0 < 4; i0++)
        acc[i0] = 0;

    for (uint k0 = 0; k0 < K; k0 += TILE_K)
    {
        uint k = k0 + loadK;
        uint tap = k / Cin;
        uint cin = k % Cin;
        int dy = (int)((tap / KW) * DilH);
        int dx = (int)((tap % KW) * DilW);
        [unroll] for (uint l = 0; l < LOADS; l++)
        {
            uint q = loadQ + l * (256 / TILE_K);
            float4 xv;
            float4 wv;
            [unroll] for (uint c = 0; c < 4; c++)
            {
                int ih = baseH[l * 4 + c] + dy;
                int iw = baseW[l * 4 + c] + dx;
                bool inside = k < K && ih >= 0 && iw >= 0 && ih < (int)InH && iw < (int)InW;
                xv[c] = inside ? X[((uint)ih * InW + (uint)iw) * InStride + InOffset + cin] : 0;
                uint col = colBase + q + 16 * c;
                wv[c] = (col < Cout && k < K) ? W[col * K + k] : 0;
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

    uint channels = Cout / R;
    [unroll] for (uint i = 0; i < 4; i++)
    {
        uint row = rowBase + thread.y * 4 + i;
        if (row >= M)
            continue;
        uint oh = row / OutW;
        uint ow = row % OutW;
        [unroll] for (uint j = 0; j < 4; j++)
        {
            uint col = colBase + thread.x + 16 * j;
            if (col >= Cout)
                continue;
            float v = acc[i][j];
            if (Flags & FLAG_BIAS)
                v += Bias[col];
            uint part = col / channels;
            uint c = col % channels;
            uint h = Shuffle == SHUFFLE_H ? oh * R + part : oh;
            uint w = Shuffle == SHUFFLE_W ? ow * R + part : ow;
            uint index = (h * StoreW + w) * OutStride + OutOffset + c;
            if (Flags & FLAG_ACCUMULATE)
                v += Y[index];
            Y[index] = v;
        }
    }
}

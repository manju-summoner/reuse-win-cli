// RE-USE の InstanceNorm の統計の部分和。1グループが Positions 個の位置を受け持ち、チャンネルごとの和と2乗和を書き出す。
// 桁落ちを抑えるため、最初の位置の値（Pivot）を引いてから足す。Pivot も一緒に書き出す（適用する側は書き換え中の先頭の値を読めないため）。

cbuffer Constants : register(b0)
{
    uint Count;
    uint Stride;
    uint Offset;
    uint Channels;
};

#define LANES 16
#define POSITIONS 8192

StructuredBuffer<float> X : register(t0);
RWStructuredBuffer<float> Partial : register(u0);

groupshared float sums[LANES][64];
groupshared float squares[LANES][64];

[numthreads(64, LANES, 1)]
void main(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    uint c = thread.x;
    uint lane = thread.y;
    float pivot = c < Channels ? X[Offset + c] : 0;
    float sum = 0;
    float square = 0;
    uint begin = group.x * POSITIONS;
    if (c < Channels)
    {
        for (uint i = lane; i < POSITIONS; i += LANES)
        {
            uint p = begin + i;
            if (p >= Count)
                break;
            float v = X[p * Stride + Offset + c] - pivot;
            sum += v;
            square = mad(v, v, square);
        }
    }
    sums[lane][c] = sum;
    squares[lane][c] = square;
    GroupMemoryBarrierWithGroupSync();

    if (lane == 0 && c < Channels)
    {
        [unroll] for (uint l = 1; l < LANES; l++)
        {
            sum += sums[l][c];
            square += squares[l][c];
        }
        Partial[(group.x * Channels + c) * 3 + 0] = sum;
        Partial[(group.x * Channels + c) * 3 + 1] = square;
        Partial[(group.x * Channels + c) * 3 + 2] = pivot;
    }
}

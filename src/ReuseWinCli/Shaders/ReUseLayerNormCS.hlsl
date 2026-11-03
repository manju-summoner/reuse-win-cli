// RE-USE の LayerNorm と残差の加算。Y には残差（ブロックへの入力）が入っていて、Y = LayerNorm(X) + Y で上書きする。
// Channels は MAX_CHANNELS まで。

cbuffer Constants : register(b0)
{
    uint Rows;
    uint Channels;
    float Epsilon;
    uint Pad0;
};

StructuredBuffer<float> X : register(t0);
StructuredBuffer<float> Gamma : register(t1);
StructuredBuffer<float> Beta : register(t2);
RWStructuredBuffer<float> Y : register(u0);

#define ROWS_PER_GROUP 64
#define MAX_CHANNELS 64

// 1 グループが 64 行を受け持つ。行をまとめて共有メモリーに読み込み（グローバルメモリーの読み書きをまとめるため）、
// 各スレッドが 1 行の平均と分散を求める。行の長さを 1 つ余らせて、行ごとに読むときのバンクの衝突を避ける
groupshared float tile[ROWS_PER_GROUP][MAX_CHANNELS + 1];
groupshared float rowMean[ROWS_PER_GROUP];
groupshared float rowScale[ROWS_PER_GROUP];

[numthreads(ROWS_PER_GROUP, 1, 1)]
void main(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    uint rowBase = group.x * ROWS_PER_GROUP;
    uint count = min(ROWS_PER_GROUP, Rows - rowBase) * Channels;
    for (uint i = thread.x; i < count; i += ROWS_PER_GROUP)
        tile[i / Channels][i % Channels] = X[rowBase * Channels + i];
    GroupMemoryBarrierWithGroupSync();

    uint r = thread.x;
    if (rowBase + r < Rows)
    {
        float mean = 0;
        for (uint c = 0; c < Channels; c++)
            mean += tile[r][c];
        mean /= Channels;

        float variance = 0;
        for (uint cv = 0; cv < Channels; cv++)
        {
            float diff = tile[r][cv] - mean;
            variance = mad(diff, diff, variance);
        }
        variance /= Channels;

        rowMean[r] = mean;
        rowScale[r] = rsqrt(variance + Epsilon);
    }
    GroupMemoryBarrierWithGroupSync();

    for (uint o = thread.x; o < count; o += ROWS_PER_GROUP)
    {
        uint ro = o / Channels;
        uint co = o % Channels;
        Y[rowBase * Channels + o] += mad((tile[ro][co] - rowMean[ro]) * rowScale[ro], Gamma[co], Beta[co]);
    }
}

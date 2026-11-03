// RE-USE の InstanceNorm（affine）と PReLU。ReUseNormStatsCS の部分和からチャンネルごとの平均と分散を求め、その場で書き換える。
// 部分和の数（Blocks）は数百程度なので、各スレッドが自分のチャンネルの部分和を足し直す。

cbuffer Constants : register(b0)
{
    uint Count;
    uint Stride;
    uint Offset;
    uint Channels;
    uint Blocks;
    float Epsilon;
    uint Pad0;
    uint Pad1;
};

StructuredBuffer<float> Partial : register(t0);
StructuredBuffer<float> Gamma : register(t1);
StructuredBuffer<float> Beta : register(t2);
StructuredBuffer<float> Slope : register(t3);
RWStructuredBuffer<float> Y : register(u0);

groupshared float means[64];
groupshared float scales[64];
groupshared float pivots[64];

[numthreads(64, 4, 1)]
void main(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    uint c = thread.x;
    if (thread.y == 0 && c < Channels)
    {
        float sum = 0;
        float square = 0;
        for (uint b = 0; b < Blocks; b++)
        {
            sum += Partial[(b * Channels + c) * 3 + 0];
            square += Partial[(b * Channels + c) * 3 + 1];
        }
        float shiftedMean = sum / Count;
        float variance = max(square / Count - shiftedMean * shiftedMean, 0);
        means[c] = shiftedMean;
        scales[c] = rsqrt(variance + Epsilon);
        pivots[c] = Partial[c * 3 + 2];
    }
    GroupMemoryBarrierWithGroupSync();

    if (c >= Channels)
        return;
    for (uint i = thread.y; i < 256; i += 4)
    {
        uint p = group.x * 256 + i;
        if (p >= Count)
            break;
        uint index = p * Stride + Offset + c;
        float v = (Y[index] - pivots[c] - means[c]) * scales[c];
        v = mad(v, Gamma[c], Beta[c]);
        Y[index] = v >= 0 ? v : v * Slope[c];
    }
}

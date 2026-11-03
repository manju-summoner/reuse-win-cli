using System.Runtime.InteropServices;

namespace ReuseWinCli.Enhancement
{
    /// <summary>
    /// RE-USE のエンコーダー・デコーダーと iSTFT で使うカーネルの呼び出し。活性は [高さ（時間）][幅（周波数）][チャンネル] の並び
    /// </summary>
    internal static class ReUseKernels
    {
        public const int NormChannels = 64;
        const int NormPositionsPerBlock = 8192;
        const int NormPositionsPerApplyGroup = 256;

        public enum Shuffle : uint
        {
            None = 0,
            Height = 1,
            Width = 2,
        }

        /// <summary>
        /// PyTorch の Conv2d の重み [出力][入力][行][列] から、入力チャンネル [sliceStart, sliceStart+sliceChannels) の分を取り出し、
        /// カーネルの並び [出力][行][列][入力] に並べ替える。swapKernel なら行と列を入れ替える
        /// </summary>
        public static float[] ReorderConvWeight(float[] weight, int cout, int cinTotal, int kernelH, int kernelW, int sliceStart, int sliceChannels, bool swapKernel)
        {
            var outH = swapKernel ? kernelW : kernelH;
            var outW = swapKernel ? kernelH : kernelW;
            var data = new float[cout * outH * outW * sliceChannels];
            for (var co = 0; co < cout; co++)
                for (var ci = 0; ci < sliceChannels; ci++)
                    for (var y = 0; y < kernelH; y++)
                        for (var x = 0; x < kernelW; x++)
                        {
                            var source = ((co * cinTotal + sliceStart + ci) * kernelH + y) * kernelW + x;
                            var (ty, tx) = swapKernel ? (x, y) : (y, x);
                            data[((co * outH + ty) * outW + tx) * sliceChannels + ci] = weight[source];
                        }
            return data;
        }

        /// <param name="weight"><see cref="ReorderConvWeight"/> で並べ替えた重み</param>
        /// <param name="outHeight">畳み込みの出力の高さ（Shuffle の前）</param>
        /// <param name="storeWidth">出力のバッファの幅（Shuffle が Width なら outWidth × factor）</param>
        /// <param name="accumulate">出力に足し込む（DenseBlock の連結した入力を、64 チャンネルずつ別のバッファから足し込むとき）</param>
        public static void Conv2d(
            ReUseComputeDevice device,
            ReUseBuffer input, int inHeight, int inWidth, int inStride, int cin,
            ReUseBuffer weight, ReUseBuffer? bias, int cout, int kernelH, int kernelW,
            ReUseBuffer output, int outHeight, int outWidth, int storeWidth,
            int strideH, int strideW, int dilationH, int padH, int padW,
            Shuffle shuffle, int factor, bool accumulate)
        {
            var flags = (bias is not null ? 1u : 0) | (accumulate ? 2u : 0);
            var constants = new ConvConstants(
                (uint)inHeight, (uint)inWidth, (uint)inStride, 0,
                (uint)cin, (uint)outHeight, (uint)outWidth, (uint)cout,
                (uint)kernelH, (uint)kernelW, (uint)strideH, (uint)strideW,
                (uint)dilationH, 1, (uint)padH, (uint)padW,
                (uint)(cout / factor), 0, (uint)shuffle, (uint)factor,
                (uint)storeWidth, flags);
            device.Dispatch("ReUseConv2dCS", constants, Groups(cout, 64), Groups(outHeight * outWidth, 64), [input, weight, bias], output);
        }

        public static int NormPartialLength(int count) => Groups(count, NormPositionsPerBlock) * NormChannels * 3;

        /// <summary>
        /// InstanceNorm2d（affine）と PReLU をその場で行う。data は [count][64]
        /// </summary>
        public static void InstanceNormPRelu(
            ReUseComputeDevice device, ReUseBuffer data, int count, ReUseBuffer partial,
            ReUseBuffer gamma, ReUseBuffer beta, ReUseBuffer slope, float epsilon)
        {
            var blocks = Groups(count, NormPositionsPerBlock);
            device.Dispatch("ReUseNormStatsCS", new NormStatsConstants((uint)count, NormChannels, 0, NormChannels), blocks, 1, [data], partial);
            device.Dispatch("ReUseNormApplyCS", new NormApplyConstants((uint)count, NormChannels, 0, NormChannels, (uint)blocks, epsilon),
                Groups(count, NormPositionsPerApplyGroup), 1, [partial, gamma, beta, slope], data);
        }

        /// <summary>Y[rows][outputs] = X[rows][inputs]・Wᵀ（W は [outputs][inputs]）</summary>
        public static void Linear(ReUseComputeDevice device, ReUseBuffer x, ReUseBuffer weight, ReUseBuffer y, int rows, int outputs, int inputs)
        {
            var constants = new LinearConstants((uint)rows, (uint)outputs, (uint)inputs, 0, (uint)inputs, 0, (uint)outputs, 0, 0, 0, 0, 0, 0);
            device.Dispatch("ReUseLinearCS", constants, Groups(outputs, 64), Groups(rows, 64), [x, weight], y);
        }

        static int Groups(int count, int size) => (count + size - 1) / size;

        [StructLayout(LayoutKind.Sequential)]
        readonly record struct ConvConstants(
            uint InH, uint InW, uint InStride, uint InOffset,
            uint Cin, uint OutH, uint OutW, uint Cout,
            uint KH, uint KW, uint StrideH, uint StrideW,
            uint DilH, uint DilW, uint PadH, uint PadW,
            uint OutStride, uint OutOffset, uint Shuffle, uint R,
            uint StoreW, uint Flags);

        [StructLayout(LayoutKind.Sequential)]
        readonly record struct NormStatsConstants(uint Count, uint Stride, uint Offset, uint Channels);

        [StructLayout(LayoutKind.Sequential)]
        readonly record struct NormApplyConstants(uint Count, uint Stride, uint Offset, uint Channels, uint Blocks, float Epsilon);

        [StructLayout(LayoutKind.Sequential)]
        readonly record struct LinearConstants(
            uint M, uint N, uint K, uint Flags,
            uint XStride, uint XOffset, uint YStride, uint YOffset,
            uint RStride, uint ROffset,
            uint ConvLength, uint ConvPosStride, int ConvDirection);
    }
}

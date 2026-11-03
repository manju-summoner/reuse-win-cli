using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ReuseWinCli.Enhancement
{
    /// <summary>
    /// RE-USE の TF-Mamba ブロック（時間方向と周波数方向の双方向 Mamba）30 段を GPU で実行する。
    /// 活性は [時間][周波数][チャンネル] の並び（チャンネルが最も内側）で持つ。
    /// 時間方向の系列も周波数方向の系列も同じ並びのまま、行の歩幅を変えてたどるので、並べ替えは要らない。
    /// </summary>
    internal sealed class ReUseMambaStack : IDisposable
    {
        public const int Channels = 64;
        const int Inner = 256;
        const int State = 16;
        const int DtRank = 4;
        const int ConvKernel = 4;
        const int DblWidth = DtRank + State * 2;
        const float NormEpsilon = 1e-5f;

        readonly ReUseComputeDevice device;
        readonly List<IDisposable> owned = [];
        readonly Block[] blocks;

        int capacityRows;
        ReUseBuffer? xBuffer;
        ReUseBuffer? zBuffer;
        ReUseBuffer? convBuffer;
        ReUseBuffer? dblBuffer;
        ReUseBuffer? scanBuffer;
        ReUseBuffer? concatBuffer;
        ReUseBuffer? projectedBuffer;

        public ReUseMambaStack(ReUseComputeDevice device, IReadOnlyDictionary<string, SafeTensor> tensors, int blockCount)
        {
            this.device = device;
            blocks = new Block[blockCount];
            try
            {
                for (var i = 0; i < blockCount; i++)
                {
                    var prefix = $"TSMamba.{i}.";
                    blocks[i] = new Block(LoadBiMamba(tensors, prefix + "time_mamba."), LoadBiMamba(tensors, prefix + "freq_mamba."));
                }
            }
            catch
            {
                // 途中までに作った重みのバッファを共有のデバイスに残さない
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// 行数の上限。この範囲なら、Linear と LayerNorm（64 行で 1 グループ）の Dispatch はグループ数の上限（65535）に収まる。
        /// selective scan は 1 系列で 1 グループで、系列の数（ビン数かフレーム数）はチャンクの長さの上限があるので届かない。
        /// 作業用バッファの大きさ（行数 × Inner × 4 バイト）は、呼び出し側が <see cref="GetLargestBufferBytes"/> で GPU の予算に収める
        /// </summary>
        public const int MaxRows = 65535 * 4;

        /// <summary>rows 行を処理するときの、いちばん大きい作業用バッファのバイト数</summary>
        public static long GetLargestBufferBytes(int rows) => (long)rows * Inner * sizeof(float);

        /// <param name="activation">[frames][bins][Channels] の活性。結果で上書きする</param>
        public void Run(ReUseBuffer activation, int frames, int bins)
        {
            var rows = frames * bins;
            if (rows <= 0 || rows > MaxRows)
                throw new ArgumentOutOfRangeException(nameof(frames), $"{frames}×{bins} 行は扱えません。");
            if (activation.Length < rows * Channels)
                throw new ArgumentException("活性のバッファが足りません。", nameof(activation));

            EnsureCapacity(rows);
            foreach (var block in blocks)
            {
                RunBiMamba(block.Time, activation, rows, sequences: bins, length: frames, seqStride: 1, posStride: bins);
                RunBiMamba(block.Freq, activation, rows, sequences: frames, length: bins, seqStride: bins, posStride: 1);
            }
        }

        void RunBiMamba(BiMamba weights, ReUseBuffer activation, int rows, int sequences, int length, int seqStride, int posStride)
        {
            RunMamba(weights.Forward, activation, rows, sequences, length, seqStride, posStride, direction: 1, concatOffset: 0);
            RunMamba(weights.Backward, activation, rows, sequences, length, seqStride, posStride, direction: -1, concatOffset: Channels);

            Linear(concatBuffer!, Channels * 2, 0, weights.OutputWeight, weights.OutputBias, null, 0, 0,
                projectedBuffer!, Channels, 0, rows, Channels, Channels * 2);

            var normConstants = new LayerNormConstants((uint)rows, Channels, NormEpsilon);
            device.Dispatch("ReUseLayerNormCS", normConstants, Groups(rows, 64), 1,
                [projectedBuffer, weights.NormGamma, weights.NormBeta], activation);
        }

        void RunMamba(Mamba weights, ReUseBuffer activation, int rows, int sequences, int length, int seqStride, int posStride, int direction, int concatOffset)
        {
            Linear(activation, Channels, 0, weights.InX, null, null, 0, 0, xBuffer!, Inner, 0, rows, Inner, Channels);
            Linear(activation, Channels, 0, weights.InZ, null, null, 0, 0, zBuffer!, Inner, 0, rows, Inner, Channels);

            // 深さ方向の因果畳み込みと SiLU は x_proj の読み込みの中で行い、結果を convBuffer にも書く
            var convConstants = new LinearConstants(
                (uint)rows, DblWidth, Inner, 0,
                Inner, 0, DblWidth, 0,
                0, 0,
                (uint)length, (uint)posStride, direction);
            device.Dispatch("ReUseLinearConvCS", convConstants, Groups(DblWidth, 64), Groups(rows, 64),
                [xBuffer, weights.XProj, null, null, weights.ConvWeight, weights.ConvBias], dblBuffer!, convBuffer);

            var scanConstants = new ScanConstants(
                (uint)sequences, (uint)length, (uint)seqStride, (uint)posStride, Inner, direction,
                DblWidth, DtRank, DtRank + State, Inner, 0);
            device.Dispatch("ReUseSelectiveScanCS", scanConstants, Inner / 64, sequences,
                [convBuffer, dblBuffer, zBuffer, weights.A, weights.D, weights.DtWeight, weights.DtBias], scanBuffer!);

            // 双方向 Mamba の各枝は入力を残差として足す（out = Mamba(x) + x）
            Linear(scanBuffer!, Inner, 0, weights.OutProj, null, activation, Channels, 0,
                concatBuffer!, Channels * 2, concatOffset, rows, Channels, Inner);
        }

        void Linear(
            ReUseBuffer x, int xStride, int xOffset,
            ReUseBuffer weight, ReUseBuffer? bias,
            ReUseBuffer? residual, int residualStride, int residualOffset,
            ReUseBuffer y, int yStride, int yOffset,
            int rows, int outputs, int inputs)
        {
            var flags = (bias is not null ? 1u : 0) | (residual is not null ? 4u : 0);
            var constants = new LinearConstants(
                (uint)rows, (uint)outputs, (uint)inputs, flags,
                (uint)xStride, (uint)xOffset, (uint)yStride, (uint)yOffset,
                (uint)residualStride, (uint)residualOffset,
                0, 0, 0);
            device.Dispatch("ReUseLinearCS", constants, Groups(outputs, 64), Groups(rows, 64), [x, weight, bias, residual], y);
        }

        static int Groups(int count, int size) => (count + size - 1) / size;

        void EnsureCapacity(int rows)
        {
            if (rows <= capacityRows)
                return;
            DisposeWork();
            xBuffer = device.CreateBuffer(rows * Inner);
            zBuffer = device.CreateBuffer(rows * Inner);
            convBuffer = device.CreateBuffer(rows * Inner);
            dblBuffer = device.CreateBuffer(rows * DblWidth);
            scanBuffer = device.CreateBuffer(rows * Inner);
            concatBuffer = device.CreateBuffer(rows * Channels * 2);
            projectedBuffer = device.CreateBuffer(rows * Channels);
            capacityRows = rows;
        }

        /// <summary>作業用バッファを手放す。次の <see cref="Run"/> で作り直す</summary>
        public void ReleaseWork() => DisposeWork();

        void DisposeWork()
        {
            xBuffer?.Dispose();
            zBuffer?.Dispose();
            convBuffer?.Dispose();
            dblBuffer?.Dispose();
            scanBuffer?.Dispose();
            concatBuffer?.Dispose();
            projectedBuffer?.Dispose();
            xBuffer = zBuffer = convBuffer = dblBuffer = scanBuffer = concatBuffer = projectedBuffer = null;
            capacityRows = 0;
        }

        BiMamba LoadBiMamba(IReadOnlyDictionary<string, SafeTensor> tensors, string prefix)
        {
            return new BiMamba(
                LoadMamba(tensors, prefix + "forward_blocks."),
                LoadMamba(tensors, prefix + "backward_blocks."),
                Upload(tensors, prefix + "output_proj.weight", Channels, Channels * 2),
                Upload(tensors, prefix + "output_proj.bias", Channels),
                Upload(tensors, prefix + "norm.weight", Channels),
                Upload(tensors, prefix + "norm.bias", Channels));
        }

        Mamba LoadMamba(IReadOnlyDictionary<string, SafeTensor> tensors, string prefix)
        {
            var inProj = Get(tensors, prefix + "in_proj.weight", Inner * 2, Channels);
            var aLog = Get(tensors, prefix + "A_log", Inner, State);
            var a = new float[aLog.Data.Length];
            for (var i = 0; i < a.Length; i++)
                a[i] = -MathF.Exp(aLog.Data[i]);

            return new Mamba(
                Own(device.CreateBuffer(inProj.Data.AsSpan(0, Inner * Channels))),
                Own(device.CreateBuffer(inProj.Data.AsSpan(Inner * Channels, Inner * Channels))),
                Upload(tensors, prefix + "conv1d.weight", Inner, 1, ConvKernel),
                Upload(tensors, prefix + "conv1d.bias", Inner),
                Upload(tensors, prefix + "x_proj.weight", DblWidth, Inner),
                Upload(tensors, prefix + "dt_proj.weight", Inner, DtRank),
                Upload(tensors, prefix + "dt_proj.bias", Inner),
                Own(device.CreateBuffer(a)),
                Upload(tensors, prefix + "D", Inner),
                Upload(tensors, prefix + "out_proj.weight", Channels, Inner));
        }

        ReUseBuffer Upload(IReadOnlyDictionary<string, SafeTensor> tensors, string name, params int[] shape)
            => Own(device.CreateBuffer(Get(tensors, name, shape).Data));

        static SafeTensor Get(IReadOnlyDictionary<string, SafeTensor> tensors, string name, params int[] shape)
        {
            if (!tensors.TryGetValue(name, out var tensor))
                throw new KeyNotFoundException($"重み {name} がありません。");
            if (!tensor.Shape.AsSpan().SequenceEqual(shape))
                throw new ArgumentException($"重み {name} の形 [{string.Join(",", tensor.Shape)}] が想定 [{string.Join(",", shape)}] と違います。");
            return tensor;
        }

        ReUseBuffer Own(ReUseBuffer buffer)
        {
            owned.Add(buffer);
            return buffer;
        }

        public void Dispose()
        {
            DisposeWork();
            foreach (var item in owned)
                item.Dispose();
            owned.Clear();
        }

        sealed record Mamba(
            ReUseBuffer InX, ReUseBuffer InZ,
            ReUseBuffer ConvWeight, ReUseBuffer ConvBias,
            ReUseBuffer XProj, ReUseBuffer DtWeight, ReUseBuffer DtBias,
            ReUseBuffer A, ReUseBuffer D, ReUseBuffer OutProj);

        sealed record BiMamba(Mamba Forward, Mamba Backward, ReUseBuffer OutputWeight, ReUseBuffer OutputBias, ReUseBuffer NormGamma, ReUseBuffer NormBeta);

        sealed record Block(BiMamba Time, BiMamba Freq);

        [StructLayout(LayoutKind.Sequential)]
        readonly record struct LinearConstants(
            uint M, uint N, uint K, uint Flags,
            uint XStride, uint XOffset, uint YStride, uint YOffset,
            uint RStride, uint ROffset,
            uint ConvLength, uint ConvPosStride, int ConvDirection);

        [StructLayout(LayoutKind.Sequential)]
        readonly record struct ScanConstants(
            uint Sequences, uint Length, uint SeqStride, uint PosStride,
            uint Channels, int Direction, uint DblStride, uint BOffset,
            uint COffset, uint ZStride, uint ZOffset);

        [StructLayout(LayoutKind.Sequential)]
        readonly record struct LayerNormConstants(uint Rows, uint Channels, float Epsilon);
    }
}

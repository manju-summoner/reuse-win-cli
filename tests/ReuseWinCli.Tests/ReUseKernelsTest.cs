using System;
using System.Linq;
using NUnit.Framework;
using ReuseWinCli.Enhancement;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// RE-USE のエンコーダー・デコーダーの部品（畳み込み・InstanceNorm と PReLU）と STFT を、乱数の入力で CPU の参照実装と比べる。
    /// 参照実装は PyTorch と同じ [チャンネル][高さ][幅] の並び・[出力][入力][行][列] の重みで書き、GPU 側の並び替えとは独立にしてある
    /// </summary>
    class ReUseKernelsTest
    {
        public sealed record ConvCase(string Name, int Cin, int Cout, int KernelH, int KernelW, int StrideH, int StrideW, int DilationH, int PadH, int PadW, int Factor, bool SwapToHeight, int Slices)
        {
            public override string ToString() => Name;
        }

        static readonly ConvCase[] ConvCases =
        [
            // DenseBlock の 2 段目（時間方向に 2 倍の拡張・入力 128 チャンネルを 2 つのバッファから足し込む）
            new("dense", 128, 64, 3, 3, 1, 1, 2, 2, 1, 1, false, 2),
            // DenseBlock の 4 段目（拡張 8・入力 4 つ）
            new("dense8", 256, 64, 3, 3, 1, 1, 8, 8, 1, 1, false, 4),
            // dense_conv_1（入力 2 チャンネルの 1×1）
            new("input", 2, 64, 1, 1, 1, 1, 1, 0, 0, 1, false, 1),
            // dense_conv_2（(1,3)・ストライド (4,2)・詰め物なし）
            new("downsample", 64, 64, 1, 3, 4, 2, 1, 0, 0, 1, false, 1),
            // up_conv1（周波数方向に 2 倍の SPConvTranspose2d）
            new("upsampleWidth", 64, 128, 1, 3, 1, 1, 1, 0, 1, 2, false, 1),
            // up_conv2（公式は時間と周波数を入れ替えてから (1,3) で 4 倍。GPU は (3,1) で時間方向に 4 倍）
            new("upsampleHeight", 64, 256, 1, 3, 1, 1, 1, 0, 1, 4, true, 1),
            // 位相のデコーダーの最後（2 チャンネルの 1×1）
            new("final", 64, 2, 1, 1, 1, 1, 1, 0, 0, 1, false, 1),
        ];

        [TestCaseSource(nameof(ConvCases))]
        public void Conv2dMatchesReference(ConvCase c)
        {
            var random = new Random(c.Name.GetHashCode() & 0xFFFF);
            const int height = 23;
            const int width = 19;
            var input = new Map(c.Cin, height, width);
            for (var i = 0; i < input.Data.Length; i++)
                input.Data[i] = random.NextDouble() * 2 - 1;
            var weight = ReUseMambaStackTest.Uniform(random, c.Cout * c.Cin * c.KernelH * c.KernelW, -0.3, 0.3);
            var bias = ReUseMambaStackTest.Uniform(random, c.Cout, -0.5, 0.5);

            // 参照: up_conv2 は公式どおり時間と周波数を入れ替えて畳み込み、元に戻す
            var expected = c.SwapToHeight
                ? Transpose(ReferenceConv(Transpose(input), weight, bias, c.Cout, c.KernelH, c.KernelW, c.StrideH, c.StrideW, c.PadH, c.PadW, c.Factor, c.DilationH))
                : ReferenceConv(input, weight, bias, c.Cout, c.KernelH, c.KernelW, c.StrideH, c.StrideW, c.PadH, c.PadW, c.Factor, c.DilationH);

            using var device = ReUseComputeDevice.Create();
            var sliceChannels = c.Cin / c.Slices;
            var (kernelH, kernelW) = c.SwapToHeight ? (c.KernelW, c.KernelH) : (c.KernelH, c.KernelW);
            var (padH, padW) = c.SwapToHeight ? (c.PadW, c.PadH) : (c.PadH, c.PadW);
            var outHeight = (height + 2 * padH - c.DilationH * (kernelH - 1) - 1) / c.StrideH + 1;
            var outWidth = (width + 2 * padW - (kernelW - 1) - 1) / c.StrideW + 1;
            var shuffle = c.Factor == 1 ? ReUseKernels.Shuffle.None : c.SwapToHeight ? ReUseKernels.Shuffle.Height : ReUseKernels.Shuffle.Width;
            var storeHeight = shuffle == ReUseKernels.Shuffle.Height ? outHeight * c.Factor : outHeight;
            var storeWidth = shuffle == ReUseKernels.Shuffle.Width ? outWidth * c.Factor : outWidth;
            var channels = c.Cout / c.Factor;

            using var biasBuffer = device.CreateBuffer(bias);
            using var output = device.CreateBuffer(storeHeight * storeWidth * channels);
            for (var s = 0; s < c.Slices; s++)
            {
                // 入力はスライスごとに別のバッファ（DenseBlock と同じ）
                var slice = new float[height * width * sliceChannels];
                for (var ci = 0; ci < sliceChannels; ci++)
                    for (var h = 0; h < height; h++)
                        for (var w = 0; w < width; w++)
                            slice[(h * width + w) * sliceChannels + ci] = (float)input[s * sliceChannels + ci, h, w];
                using var sliceBuffer = device.CreateBuffer(slice);
                using var weightBuffer = device.CreateBuffer(
                    ReUseKernels.ReorderConvWeight(weight, c.Cout, c.Cin, c.KernelH, c.KernelW, s * sliceChannels, sliceChannels, c.SwapToHeight));
                ReUseKernels.Conv2d(device, sliceBuffer, height, width, sliceChannels, sliceChannels,
                    weightBuffer, s == 0 ? biasBuffer : null, c.Cout, kernelH, kernelW,
                    output, outHeight, outWidth, storeWidth, c.StrideH, c.StrideW, c.DilationH, padH, padW, shuffle, c.Factor, accumulate: s > 0);
            }
            var actual = new float[storeHeight * storeWidth * channels];
            output.Readback(actual);

            Assert.That((expected.Channels, expected.Height, expected.Width), Is.EqualTo((channels, storeHeight, storeWidth)));
            var error = 0d;
            for (var ch = 0; ch < channels; ch++)
                for (var h = 0; h < storeHeight; h++)
                    for (var w = 0; w < storeWidth; w++)
                        error = Math.Max(error, Math.Abs(actual[(h * storeWidth + w) * channels + ch] - expected[ch, h, w]));
            TestContext.Out.WriteLine($"max error {error:E3}");
            Assert.That(error, Is.LessThan(1e-4));
        }

        /// <param name="mean">値の平均。大きくすると、分散を 2 乗の和から求めるときの桁落ちを検証できる</param>
        [TestCase(30000, 0.0)]
        [TestCase(20000, 50.0)]
        [TestCase(100, 0.0)]
        public void InstanceNormPReluMatchesReference(int count, double mean)
        {
            const int channels = ReUseKernels.NormChannels;
            var random = new Random(count);
            var data = new double[count * channels];
            for (var p = 0; p < count; p++)
                for (var ch = 0; ch < channels; ch++)
                    data[p * channels + ch] = mean + (ch + 1) * 0.01 * (random.NextDouble() * 2 - 1);
            var gamma = ReUseMambaStackTest.Uniform(random, channels, 0.5, 1.5);
            var beta = ReUseMambaStackTest.Uniform(random, channels, -0.2, 0.2);
            var slope = ReUseMambaStackTest.Uniform(random, channels, 0, 0.5);

            var expected = new double[data.Length];
            for (var ch = 0; ch < channels; ch++)
            {
                var m = Enumerable.Range(0, count).Average(p => data[p * channels + ch]);
                var variance = Enumerable.Range(0, count).Average(p => (data[p * channels + ch] - m) * (data[p * channels + ch] - m));
                var scale = 1 / Math.Sqrt(variance + 1e-5);
                for (var p = 0; p < count; p++)
                {
                    var v = (data[p * channels + ch] - m) * scale * gamma[ch] + beta[ch];
                    expected[p * channels + ch] = v >= 0 ? v : v * slope[ch];
                }
            }

            using var device = ReUseComputeDevice.Create();
            using var buffer = device.CreateBuffer(data.Select(v => (float)v).ToArray());
            using var partial = device.CreateBuffer(ReUseKernels.NormPartialLength(count));
            using var gammaBuffer = device.CreateBuffer(gamma);
            using var betaBuffer = device.CreateBuffer(beta);
            using var slopeBuffer = device.CreateBuffer(slope);
            ReUseKernels.InstanceNormPRelu(device, buffer, count, partial, gammaBuffer, betaBuffer, slopeBuffer, 1e-5f);
            var actual = new float[data.Length];
            buffer.Readback(actual);

            var error = actual.Zip(expected, (a, b) => Math.Abs(a - b)).Max();
            TestContext.Out.WriteLine($"max error {error:E3}");
            // 平均 50 の入力は fp32 に入れた時点で 4e-6 の丸めがあり、標準偏差 0.006 で割ると 1e-3 程度になる
            Assert.That(error, Is.LessThan(mean == 0 ? 1e-4 : 3e-3));
        }

        /// <summary>
        /// STFT とモデルの入力（振幅の log1p と位相）を、素朴な倍精度の DFT と比べる。
        /// 位相は丸めの残りの扱いで ±π が入れ替わりやすいので、torch と同じ規約（DC と Nyquist の虚部は 0、-0.0 は +0.0）を確かめる
        /// </summary>
        [TestCase(11025)]
        [TestCase(8000)]
        public void ModelInputMatchesNaiveDft(int sampleRate)
        {
            var (fft, hop) = ReUseModel.GetStftSize(sampleRate);
            var bins = fft / 2 + 1;
            var random = new Random(sampleRate);
            // 先頭は無音（-0.0 の扱い）、後半は直流を含む負の値（DC の位相 π）
            var samples = new float[fft * 3];
            for (var i = fft; i < samples.Length; i++)
                samples[i] = (float)(-0.2 + 0.1 * Math.Sin(2 * Math.PI * 300 * i / sampleRate) + 0.01 * (random.NextDouble() * 2 - 1));
            var window = Enumerable.Range(0, fft).Select(n => 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / fft)).ToArray();
            var frames = 1 + samples.Length / hop;

            var spectrum = ReUseModel.Stft(fft, hop, window, samples, frames);
            var input = ReUseModel.BuildModelInput(spectrum, frames, bins, frames + 2, bins + 2);

            var maxMagnitude = 0d;
            var maxPhase = 0d;
            for (var t = 0; t < frames; t++)
            {
                for (var k = 0; k < bins; k++)
                {
                    double re = 0, im = 0;
                    for (var n = 0; n < fft; n++)
                    {
                        var index = t * hop + n - fft / 2;
                        if (index < 0) index = -index;
                        if (index >= samples.Length) index = 2 * (samples.Length - 1) - index;
                        var angle = 2 * Math.PI * ((long)k * n % fft) / fft;
                        re += samples[index] * window[n] * Math.Cos(angle);
                        im -= samples[index] * window[n] * Math.Sin(angle);
                    }
                    if (k == 0 || k == fft / 2)
                        im = 0;
                    var magnitude = Math.Log(1 + Math.Sqrt(re * re + im * im));
                    var phase = Math.Atan2(im + 0.0, re + 0.0);
                    var i = (t * (bins + 2) + k) * 2;
                    maxMagnitude = Math.Max(maxMagnitude, Math.Abs(input[i] - magnitude));
                    // 振幅が小さいビンの位相は丸めで大きく動くので、振幅で重みを付けて比べる（±π の入れ替わりはどの振幅でも捕まえる）
                    var phaseError = Math.Abs(input[i + 1] - phase);
                    if (phaseError > 3 || Math.Sqrt(re * re + im * im) > 1e-6)
                        maxPhase = Math.Max(maxPhase, phaseError);
                }
            }
            TestContext.Out.WriteLine($"magnitude {maxMagnitude:E3} phase {maxPhase:E3}");
            Assert.That(maxMagnitude, Is.LessThan(1e-5));
            Assert.That(maxPhase, Is.LessThan(1e-4));
        }

        /// <summary>
        /// 作業用バッファの見積もりに TF-Mamba の行数の上限（Dispatch のグループ数）も入っていること。
        /// 入っていないと、高いサンプリングレートでチャンクを短くしないまま実行して例外になる
        /// </summary>
        [Test]
        public void LargestBufferAccountsForMambaRows()
        {
            Assert.That(ReUseModel.GetLargestBufferBytes(192000 * 5, 192000), Is.EqualTo(long.MaxValue));
            Assert.That(ReUseModel.GetLargestBufferBytes(192000 * 2, 192000), Is.LessThan(long.MaxValue));
            // 48kHz・5 秒はエンコーダーの入口がいちばん大きい: (1001+2) フレーム × (961+2) ビン × 64 チャンネル × 4 バイト
            Assert.That(ReUseModel.GetLargestBufferBytes(48000 * 5, 48000), Is.EqualTo(1003L * 963 * 64 * 4));
        }

        // ---- 参照実装。テンソルは [チャンネル][高さ（時間）][幅（周波数）] ----

        sealed class Map(int channels, int height, int width)
        {
            public int Channels { get; } = channels;
            public int Height { get; } = height;
            public int Width { get; } = width;
            public double[] Data { get; } = new double[channels * height * width];
            public ref double this[int c, int h, int w] => ref Data[(c * Height + h) * Width + w];
        }

        /// <param name="factor">SPConvTranspose2d の倍率（出力チャンネルを分けて幅を factor 倍にする）。1 なら普通の畳み込み</param>
        static Map ReferenceConv(Map x, float[] weight, float[] bias, int cout, int kh, int kw, int strideH, int strideW, int padH, int padW, int factor, int dilationH)
        {
            var cin = x.Channels;
            var outH = (x.Height + 2 * padH - dilationH * (kh - 1) - 1) / strideH + 1;
            var outW = (x.Width + 2 * padW - (kw - 1) - 1) / strideW + 1;
            var output = new Map(cout / factor, outH, outW * factor);
            for (var co = 0; co < cout; co++)
            {
                for (var h = 0; h < outH; h++)
                {
                    for (var w = 0; w < outW; w++)
                    {
                        var v = (double)bias[co];
                        for (var ci = 0; ci < cin; ci++)
                            for (var y = 0; y < kh; y++)
                                for (var z = 0; z < kw; z++)
                                {
                                    var ih = h * strideH - padH + y * dilationH;
                                    var iw = w * strideW - padW + z;
                                    if (ih >= 0 && iw >= 0 && ih < x.Height && iw < x.Width)
                                        v += weight[((co * cin + ci) * kh + y) * kw + z] * x[ci, ih, iw];
                                }
                        // SPConvTranspose2d: view(B, r, C/r, H, W) → permute → 幅 W·r。出力チャンネル co = part·(C/r) + c
                        var part = co / (cout / factor);
                        output[co % (cout / factor), h, w * factor + part] = v;
                    }
                }
            }
            return output;
        }

        static Map Transpose(Map x)
        {
            var output = new Map(x.Channels, x.Width, x.Height);
            for (var c = 0; c < x.Channels; c++)
                for (var h = 0; h < x.Height; h++)
                    for (var w = 0; w < x.Width; w++)
                        output[c, w, h] = x[c, h, w];
            return output;
        }
    }
}

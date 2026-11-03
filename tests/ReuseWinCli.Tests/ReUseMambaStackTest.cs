using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using ReuseWinCli.Enhancement;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// RE-USE の TF-Mamba ブロックの GPU 実装の検証。
    /// 乱数の重みで、PyTorch の nn.Module と同じ手順（系列の取り出し・反転・連結）で書いた CPU の参照実装と比べる。
    /// GPU 側は系列を並べ替えずに行の歩幅でたどり、後ろ向きの枝も反転せずに逆からたどるので、参照実装とは別の書き方になっている。
    /// </summary>
    class ReUseMambaStackTest
    {
        const int C = 64;
        const int Inner = 256;
        const int State = 16;
        const int DtRank = 4;
        const int Dbl = DtRank + State * 2;

        [TestCase(5, 9, 1)]
        [TestCase(13, 11, 2)]
        [TestCase(70, 3, 1)]
        public void GpuMatchesReference(int frames, int bins, int blockCount)
        {
            var random = new Random(frames * 1000 + bins);
            var tensors = new Dictionary<string, SafeTensor>();
            for (var i = 0; i < blockCount; i++)
            {
                AddBiMamba(tensors, random, $"TSMamba.{i}.time_mamba.");
                AddBiMamba(tensors, random, $"TSMamba.{i}.freq_mamba.");
            }
            var input = Uniform(random, frames * bins * C, -1, 1);

            var expected = ReferenceStack(tensors, input, frames, bins, blockCount);

            using var device = ReUseComputeDevice.Create();
            using var stack = new ReUseMambaStack(device, tensors, blockCount);
            using var activation = device.CreateBuffer(input);
            stack.Run(activation, frames, bins);
            var actual = new float[input.Length];
            activation.Readback(actual);

            var scale = expected.Max(v => Math.Abs(v));
            var error = MaxError(actual, expected);
            TestContext.Out.WriteLine($"max error {error:E3} (scale {scale:F3}) warp={device.IsWarp}");
            Assert.That(error, Is.LessThan(scale * 1e-4));
        }

        /// <summary>
        /// 公式の重みと PyTorch で出した中間テンソルで、30 段を通した結果を比べる。
        /// 環境変数 REUSE_FIXTURE_DIR に fixture（PyTorch で出した中間テンソル）のフォルダを指定したときだけ走る
        /// </summary>
        [Test]
        public void MatchesPyTorchWithOfficialWeights()
        {
            var fixture = Fixture.RequireDirectory("enc_out.bin");
            var tensors = Fixture.ReadOfficialWeights();
            var encoded = Fixture.ReadFloats(Path.Combine(fixture, "enc_out.bin"));
            var block0 = Fixture.ReadFloats(Path.Combine(fixture, "block0_out.bin"));
            var stackOut = Fixture.ReadFloats(Path.Combine(fixture, "stack_out.bin"));
            // meta.json の enc_out の形 [frames, bins, 64]
            const int frames = 257;
            const int bins = 442;
            Assert.That(encoded, Has.Length.EqualTo(frames * bins * C));

            using var device = ReUseComputeDevice.Create();

            using (var one = new ReUseMambaStack(device, tensors, 1))
            using (var activation = device.CreateBuffer(encoded))
            {
                one.Run(activation, frames, bins);
                var actual = new float[encoded.Length];
                activation.Readback(actual);
                var error = MaxError(actual, block0);
                TestContext.Out.WriteLine($"block0 max error {error:E3} (scale {block0.Max(Math.Abs):F3})");
                Assert.That(error, Is.LessThan(block0.Max(Math.Abs) * 1e-4));
            }

            using var stack = new ReUseMambaStack(device, tensors, 30);
            using var buffer = device.CreateBuffer(encoded.Length);
            var result = new float[encoded.Length];
            for (var trial = 0; trial < 3; trial++)
            {
                buffer.Upload(encoded);
                var watch = Stopwatch.StartNew();
                stack.Run(buffer, frames, bins);
                buffer.Readback(result);
                TestContext.Out.WriteLine($"30 blocks {frames}x{bins}: {watch.Elapsed.TotalSeconds:F3}s warp={device.IsWarp}");
            }
            var stackError = MaxError(result, stackOut);
            var rms = Math.Sqrt(stackOut.Select(v => (double)v * v).Average());
            var errRms = Math.Sqrt(result.Zip(stackOut, (a, b) => (double)(a - b) * (a - b)).Average());
            TestContext.Out.WriteLine($"stack max error {stackError:E3} rms error {errRms:E3} (rms {rms:F3})");
            Assert.That(errRms, Is.LessThan(rms * 1e-3));
        }

        static double MaxError(float[] actual, float[] expected)
        {
            var error = 0d;
            for (var i = 0; i < actual.Length; i++)
                error = Math.Max(error, Math.Abs(actual[i] - expected[i]));
            return error;
        }

        internal static float[] Uniform(Random random, int count, double min, double max)
            => [.. Enumerable.Range(0, count).Select(_ => (float)(min + (max - min) * random.NextDouble()))];

        internal static void AddBiMamba(Dictionary<string, SafeTensor> tensors, Random random, string prefix)
        {
            AddMamba(tensors, random, prefix + "forward_blocks.");
            AddMamba(tensors, random, prefix + "backward_blocks.");
            tensors[prefix + "output_proj.weight"] = new([C, C * 2], Uniform(random, C * C * 2, -0.1, 0.1));
            tensors[prefix + "output_proj.bias"] = new([C], Uniform(random, C, -0.1, 0.1));
            tensors[prefix + "norm.weight"] = new([C], Uniform(random, C, 0.5, 1.5));
            tensors[prefix + "norm.bias"] = new([C], Uniform(random, C, -0.2, 0.2));
        }

        static void AddMamba(Dictionary<string, SafeTensor> tensors, Random random, string prefix)
        {
            tensors[prefix + "in_proj.weight"] = new([Inner * 2, C], Uniform(random, Inner * 2 * C, -0.25, 0.25));
            tensors[prefix + "conv1d.weight"] = new([Inner, 1, 4], Uniform(random, Inner * 4, -0.5, 0.5));
            tensors[prefix + "conv1d.bias"] = new([Inner], Uniform(random, Inner, -0.2, 0.2));
            tensors[prefix + "x_proj.weight"] = new([Dbl, Inner], Uniform(random, Dbl * Inner, -0.1, 0.1));
            tensors[prefix + "dt_proj.weight"] = new([Inner, DtRank], Uniform(random, Inner * DtRank, -0.5, 0.5));
            tensors[prefix + "dt_proj.bias"] = new([Inner], Uniform(random, Inner, -3, -1));
            tensors[prefix + "A_log"] = new([Inner, State], Uniform(random, Inner * State, 0, 2.5));
            tensors[prefix + "D"] = new([Inner], Uniform(random, Inner, -1, 1));
            tensors[prefix + "out_proj.weight"] = new([C, Inner], Uniform(random, C * Inner, -0.1, 0.1));
        }

        // ---- PyTorch の SEMamba（mamba_block2_SEMamba.py）をそのまま書き写した参照実装 ----

        internal static float[] ReferenceStack(Dictionary<string, SafeTensor> t, float[] input, int frames, int bins, int blockCount)
        {
            var x = input.Select(v => (double)v).ToArray();
            for (var b = 0; b < blockCount; b++)
            {
                // time_mamba: [b*f, t, c]
                for (var f = 0; f < bins; f++)
                {
                    var seq = new double[frames][];
                    for (var i = 0; i < frames; i++)
                        seq[i] = x.AsSpan((i * bins + f) * C, C).ToArray();
                    var output = BiMamba(t, $"TSMamba.{b}.time_mamba.", seq);
                    for (var i = 0; i < frames; i++)
                        for (var c = 0; c < C; c++)
                            x[(i * bins + f) * C + c] += output[i][c];
                }
                // freq_mamba: [b*t, f, c]
                for (var i = 0; i < frames; i++)
                {
                    var seq = new double[bins][];
                    for (var f = 0; f < bins; f++)
                        seq[f] = x.AsSpan((i * bins + f) * C, C).ToArray();
                    var output = BiMamba(t, $"TSMamba.{b}.freq_mamba.", seq);
                    for (var f = 0; f < bins; f++)
                        for (var c = 0; c < C; c++)
                            x[(i * bins + f) * C + c] += output[f][c];
                }
            }
            return [.. x.Select(v => (float)v)];
        }

        static double[][] BiMamba(Dictionary<string, SafeTensor> t, string prefix, double[][] x)
        {
            var length = x.Length;
            var forward = Add(Mamba(t, prefix + "forward_blocks.", x), x);
            var flipped = x.Reverse().ToArray();
            var backward = Add(Mamba(t, prefix + "backward_blocks.", flipped), flipped).Reverse().ToArray();
            var concat = new double[length][];
            for (var i = 0; i < length; i++)
                concat[i] = [.. forward[i], .. backward[i]];
            var projected = LinearRows(concat, t[prefix + "output_proj.weight"].Data, t[prefix + "output_proj.bias"].Data, C, C * 2);
            var gamma = t[prefix + "norm.weight"].Data;
            var beta = t[prefix + "norm.bias"].Data;
            return [.. projected.Select(row =>
            {
                var mean = row.Average();
                var variance = row.Select(v => (v - mean) * (v - mean)).Average();
                var scale = 1 / Math.Sqrt(variance + 1e-5);
                return row.Select((v, c) => (v - mean) * scale * gamma[c] + beta[c]).ToArray();
            })];
        }

        static double[][] Mamba(Dictionary<string, SafeTensor> t, string prefix, double[][] x)
        {
            var length = x.Length;
            var xz = LinearRows(x, t[prefix + "in_proj.weight"].Data, null, Inner * 2, C);
            var convWeight = t[prefix + "conv1d.weight"].Data;
            var convBias = t[prefix + "conv1d.bias"].Data;
            var u = new double[length][];
            for (var i = 0; i < length; i++)
            {
                u[i] = new double[Inner];
                for (var d = 0; d < Inner; d++)
                {
                    var v = (double)convBias[d];
                    for (var j = 0; j < 4; j++)
                    {
                        var p = i + j - 3;
                        if (p >= 0)
                            v += convWeight[d * 4 + j] * xz[p][d];
                    }
                    u[i][d] = v / (1 + Math.Exp(-v));
                }
            }
            var dbl = LinearRows(u, t[prefix + "x_proj.weight"].Data, null, Dbl, Inner);
            var dt = LinearRows([.. dbl.Select(r => r[..DtRank])], t[prefix + "dt_proj.weight"].Data, t[prefix + "dt_proj.bias"].Data, Inner, DtRank);
            var aLog = t[prefix + "A_log"].Data;
            var skip = t[prefix + "D"].Data;
            var y = new double[length][];
            for (var i = 0; i < length; i++)
                y[i] = new double[Inner];
            for (var d = 0; d < Inner; d++)
            {
                var h = new double[State];
                for (var i = 0; i < length; i++)
                {
                    var delta = dt[i][d] > 20 ? dt[i][d] : Math.Log(1 + Math.Exp(dt[i][d]));
                    var sum = 0d;
                    for (var n = 0; n < State; n++)
                    {
                        var a = -Math.Exp(aLog[d * State + n]);
                        h[n] = Math.Exp(delta * a) * h[n] + delta * dbl[i][DtRank + n] * u[i][d];
                        sum += h[n] * dbl[i][DtRank + State + n];
                    }
                    sum += skip[d] * u[i][d];
                    var z = xz[i][Inner + d];
                    y[i][d] = sum * (z / (1 + Math.Exp(-z)));
                }
            }
            return LinearRows(y, t[prefix + "out_proj.weight"].Data, null, C, Inner);
        }

        static double[][] Add(double[][] a, double[][] b) => [.. a.Select((row, i) => row.Select((v, c) => v + b[i][c]).ToArray())];

        static double[][] LinearRows(double[][] x, float[] weight, float[]? bias, int outputs, int inputs)
        {
            return [.. x.Select(row =>
            {
                var result = new double[outputs];
                for (var n = 0; n < outputs; n++)
                {
                    var v = bias is null ? 0d : bias[n];
                    for (var k = 0; k < inputs; k++)
                        v += row[k] * weight[n * inputs + k];
                    result[n] = v;
                }
                return result;
            })];
        }
    }
}

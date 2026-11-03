using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ReuseWinCli.Audio;
using ReuseWinCli.Enhancement;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// RE-USE の推論全体（STFT からデコーダー・iSTFT まで）を、公式の重みと float64 の PyTorch の出力で検証する。
    /// 環境変数 REUSE_FIXTURE_DIR に、入力と float64 の PyTorch の出力（生の float 列）を置いたフォルダを指定したときだけ走る。
    /// 乱数の重みでは全体を比べられない（学習していない深いネットワークは条件が悪く、丸め誤差だけで出力が別物になる）ので、
    /// 部品ごとの検証は ReUseKernelsTest で行う
    /// </summary>
    class ReUseModelTest
    {
        [TestCase("", 44100, false)]
        [TestCase("_8000", 8000, false)]
        // WARP は 44.1kHz の 5 秒に 2 分かかるので、短い 8kHz だけを確かめる
        [TestCase("_8000", 8000, true)]
        public void MatchesPyTorchEndToEnd(string suffix, int sampleRate, bool warp)
        {
            var fixture = Fixture.RequireDirectory($"enhanced64{suffix}.bin");
            var input = Fixture.ReadFloats(Path.Combine(fixture, $"input{suffix}.bin"));
            var expected = Fixture.ReadFloats(Path.Combine(fixture, $"enhanced64{suffix}.bin"));

            using var device = ReUseComputeDevice.Create(warp);
            using var model = new ReUseModel(device, Fixture.ReadOfficialWeights());
            float[] actual = [];
            for (var trial = 0; trial < (warp ? 1 : 3); trial++)
            {
                var watch = Stopwatch.StartNew();
                actual = model.Enhance(input, sampleRate);
                TestContext.Out.WriteLine($"enhance {input.Length / (double)sampleRate:F2}s audio: {watch.Elapsed.TotalSeconds:F3}s warp={device.IsWarp}");
            }

            Assert.That(device.IsWarp, Is.EqualTo(warp));
            Assert.That(actual, Has.Length.EqualTo(expected.Length));
            var snr = Fixture.Snr(actual, expected);
            TestContext.Out.WriteLine($"SNR against PyTorch (float64): {snr:F1} dB");
            // PyTorch 自身の fp32（CUDA）と float64 の差は 44.1kHz で 55.8dB、8kHz で 68.7dB
            Assert.That(snr, Is.GreaterThan(60));
        }

        /// <summary>
        /// チャンクに分けて（2 秒のチャンクを半分ずつ重ねて）通したときと、1 回で通したときとの差を見る。
        /// チャンクの境目では前後の文脈が変わるので一致はしないが、位置のずれや重ね合わせの誤りがあれば 0dB 近くまで落ちる
        /// </summary>
        [Test]
        public void ChunkedStaysCloseToSinglePass()
        {
            var fixture = Fixture.RequireDirectory("input.bin");
            const int hz = 44100;
            var mono = Fixture.ReadFloats(Path.Combine(fixture, "input.bin"));
            var stereo = new float[mono.Length * 2];
            for (var i = 0; i < mono.Length; i++)
                stereo[i * 2] = stereo[i * 2 + 1] = mono[i];

            using var engine = ReUseEngine.Create(null, false);
            var single = engine.Enhance(mono, hz);

            var output = new MemoryWriter();
            var watch = Stopwatch.StartNew();
            new ChunkedEnhancer(new FixedChunkEnhancer(engine, hz * 2)).Process(new MemoryReader(stereo, hz, 2), output, 1, null, default);
            TestContext.Out.WriteLine($"chunked {mono.Length / (double)hz:F2}s audio: {watch.Elapsed.TotalSeconds:F3}s");

            var samples = output.ToArray();
            Assert.That(samples, Has.Length.EqualTo(stereo.Length));
            var left = samples.Where((_, i) => i % 2 == 0).ToArray();
            var right = samples.Where((_, i) => i % 2 == 1).ToArray();
            Assert.That(right, Is.EqualTo(left));
            var snr = Fixture.Snr(left, single);
            TestContext.Out.WriteLine($"SNR against single pass: {snr:F1} dB");
            Assert.That(snr, Is.GreaterThan(15));
        }

        sealed class FixedChunkEnhancer(ISpeechEnhancer inner, int chunkSamples) : ISpeechEnhancer
        {
            public int GetChunkSamples(int sampleRate) => chunkSamples;
            public float[] Enhance(ReadOnlySpan<float> samples, int sampleRate, System.Threading.CancellationToken token) => inner.Enhance(samples, sampleRate, token);
        }
    }
}

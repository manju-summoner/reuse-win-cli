using System;
using System.Collections.Concurrent;
using System.Linq;
using NUnit.Framework;
using ReuseWinCli.Enhancement;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// チャンクの区切り方と重ね合わせを、GPU を使わない偽の推論で検証する。
    /// 期待値は、音声全体を先に読んでチャンクごとに推論してから混ぜる素直な書き方で求め、読みながら書く本体の実装とは独立にしてある。
    /// 混ぜ方は YMM4 の音声エフェクト「AIノイズ除去（RE-USE）」と同じ式（Hann 窓の重みの加重平均と、元の音との線形の混合）
    /// </summary>
    class ChunkedEnhancerTest
    {
        const int Hop = 100;
        const int ChunkLength = Hop * 2;
        const int SampleRate = 8000;

        /// <summary>
        /// チャンクの中の位置と長さで値が変わる偽の推論。チャンクの位置や長さを取り違えると出力が変わる
        /// </summary>
        sealed class FakeEnhancer : ISpeechEnhancer
        {
            public ConcurrentBag<int> Lengths { get; } = [];

            public int GetChunkSamples(int sampleRate) => ChunkLength;

            public float[] Enhance(ReadOnlySpan<float> samples, int sampleRate, System.Threading.CancellationToken token)
            {
                Lengths.Add(samples.Length);
                return Apply(samples.ToArray());
            }

            public static float[] Apply(float[] samples)
            {
                var result = new float[samples.Length];
                for (var i = 0; i < samples.Length; i++)
                    result[i] = MathF.Tanh(samples[i] * 0.7f) + i * 1e-3f + samples.Length * 1e-5f;
                return result;
            }
        }

        [TestCase(Hop * 4, 1, 1.0)]
        [TestCase(Hop * 3 + 37, 1, 1.0)]
        [TestCase(Hop - 5, 1, 1.0)]
        [TestCase(1, 1, 1.0)]
        [TestCase(Hop * 5 + 1, 2, 1.0)]
        [TestCase(Hop * 3 + 37, 3, 0.5)]
        [TestCase(Hop * 3 + 37, 2, 0.0)]
        public void MatchesOfflineReference(int frames, int channels, double strength)
        {
            var random = new Random(frames * 10 + channels);
            var input = Enumerable.Range(0, frames * channels).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
            var enhancer = new FakeEnhancer();
            var output = new MemoryWriter();

            // 1 回に 7 サンプルずつしか返さない入力で、チャンネルの区切りに揃わない読み方にする
            new ChunkedEnhancer(enhancer).Process(new MemoryReader(input, SampleRate, channels, maxRead: 7), output, (float)strength, null, default);

            var expected = Reference(input, frames, channels, (float)strength);
            Assert.That(output.ToArray(), Is.EqualTo(expected));
            // チャンク k は位置 k*Hop から最大 ChunkLength。最後のチャンクは入力がある分だけ
            var expectedLengths = Enumerable.Range(0, (frames + Hop - 1) / Hop)
                .Select(k => Math.Min(ChunkLength, frames - k * Hop))
                .SelectMany(length => Enumerable.Repeat(length, channels));
            Assert.That(enhancer.Lengths.OrderBy(x => x), Is.EqualTo(expectedLengths.OrderBy(x => x)));
        }

        [Test]
        public void EmptyInputWritesNothing()
        {
            var enhancer = new FakeEnhancer();
            var output = new MemoryWriter();
            new ChunkedEnhancer(enhancer).Process(new MemoryReader([], SampleRate, 2), output, 1, null, default);
            Assert.That(output.ToArray(), Is.Empty);
            Assert.That(enhancer.Lengths, Is.Empty);
        }

        [Test]
        public void IdenticalChannelsAreEnhancedOnce()
        {
            const int frames = Hop * 3;
            var mono = Enumerable.Range(0, frames).Select(i => MathF.Sin(i * 0.1f)).ToArray();
            var stereo = mono.SelectMany(v => new[] { v, v }).ToArray();
            var enhancer = new FakeEnhancer();
            var output = new MemoryWriter();

            new ChunkedEnhancer(enhancer).Process(new MemoryReader(stereo, SampleRate, 2), output, 1, null, default);

            Assert.That(enhancer.Lengths, Has.Count.EqualTo(3));
            Assert.That(output.ToArray(), Is.EqualTo(Reference(stereo, frames, 2, 1)));
        }

        [Test]
        public void ReportsProgressUpToAllFrames()
        {
            const int frames = Hop * 3 + 37;
            var reported = new System.Collections.Generic.List<long>();
            new ChunkedEnhancer(new FakeEnhancer()).Process(new MemoryReader(new float[frames], SampleRate, 1), new MemoryWriter(), 1, reported.Add, default);
            Assert.That(reported, Is.Ordered.Ascending);
            Assert.That(reported[^1], Is.EqualTo(frames));
        }

        [Test]
        public void EnhancerFailureIsPropagated()
        {
            var output = new MemoryWriter();
            var enhancer = new ThrowingEnhancer();
            Assert.That(
                () => new ChunkedEnhancer(enhancer).Process(new MemoryReader(new float[Hop * 5], SampleRate, 1), output, 1, null, default),
                Throws.InvalidOperationException.With.Message.EqualTo("推論に失敗"));
        }

        sealed class ThrowingEnhancer : ISpeechEnhancer
        {
            public int GetChunkSamples(int sampleRate) => ChunkLength;
            public float[] Enhance(ReadOnlySpan<float> samples, int sampleRate, System.Threading.CancellationToken token) => throw new InvalidOperationException("推論に失敗");
        }

        /// <summary>
        /// 左右で中身の違うステレオで、両方のチャンネルの推論が中止されたとき、まとめた例外ではなく中止として伝わる（呼び出し元が「中止しました」と出せる）
        /// </summary>
        [Test]
        public void CancellationOfSeveralChannelsIsReportedAsCancellation()
        {
            const int frames = Hop * 3;
            var stereo = Enumerable.Range(0, frames * 2).Select(i => i % 2 == 0 ? MathF.Sin(i * 0.1f) : MathF.Cos(i * 0.3f)).ToArray();
            using var cancellation = new System.Threading.CancellationTokenSource();
            var enhancer = new CancellingEnhancer(cancellation);
            Assert.That(
                () => new ChunkedEnhancer(enhancer).Process(new MemoryReader(stereo, SampleRate, 2), new MemoryWriter(), 1, null, cancellation.Token),
                Throws.InstanceOf<OperationCanceledException>());
        }

        /// <summary>両方のチャンネルがそろって推論に入ってから中止し、どちらも中止の例外で終わる</summary>
        sealed class CancellingEnhancer(System.Threading.CancellationTokenSource cancellation) : ISpeechEnhancer
        {
            readonly System.Threading.Barrier barrier = new(2);
            public int GetChunkSamples(int sampleRate) => ChunkLength;
            public float[] Enhance(ReadOnlySpan<float> samples, int sampleRate, System.Threading.CancellationToken token)
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return samples.ToArray();
            }
        }

        static float[] Reference(float[] input, int frames, int channels, float strength)
        {
            var window = new float[ChunkLength];
            for (var i = 0; i < ChunkLength; i++)
                window[i] = (float)Math.Max(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / ChunkLength), 1e-7);

            var chunkCount = (frames + Hop - 1) / Hop;
            var wet = new float[chunkCount][][];
            for (var k = 0; k < chunkCount; k++)
            {
                var start = k * Hop;
                var length = Math.Min(ChunkLength, frames - start);
                wet[k] = new float[channels][];
                for (var c = 0; c < channels; c++)
                    wet[k][c] = FakeEnhancer.Apply(Enumerable.Range(start, length).Select(t => input[t * channels + c]).ToArray());
            }

            var result = new float[frames * channels];
            for (var t = 0; t < frames; t++)
            {
                var k = t / Hop;
                var a = t - k * Hop;
                for (var c = 0; c < channels; c++)
                {
                    float value;
                    if (k == 0)
                    {
                        value = wet[k][c][a];
                    }
                    else
                    {
                        var wa = window[a];
                        var wb = window[a + Hop];
                        value = (wa * wet[k][c][a] + wb * wet[k - 1][c][a + Hop]) / (wa + wb);
                    }
                    result[t * channels + c] = input[t * channels + c] * (1 - strength) + value * strength;
                }
            }
            return result;
        }
    }
}

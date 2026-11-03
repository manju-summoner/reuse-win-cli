using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReuseWinCli.Audio;

namespace ReuseWinCli.Enhancement
{
    /// <summary>
    /// 推論の差し替え口。本番は <see cref="ReUseEngine"/>、テストは GPU を使わない偽の推論を渡す
    /// </summary>
    internal interface ISpeechEnhancer
    {
        /// <summary>1 回で処理するサンプル数（偶数）</summary>
        int GetChunkSamples(int sampleRate);

        /// <summary>モノラルの音声を補正して、同じ長さで返す。別々のスレッドから同時に呼ばれる。中止されたら OperationCanceledException</summary>
        float[] Enhance(ReadOnlySpan<float> samples, int sampleRate, CancellationToken token);
    }

    /// <summary>
    /// 音声全体を RE-USE で補正する。
    /// RE-USE は前後の文脈を使う（双方向）モデルなので、公式の inference_chunk.py と同じく、一定の長さの区間（チャンク）に分けて処理し、
    /// 半分ずつ重ねて Hann 窓で混ぜる。チャンクの区切り方と混ぜ方は YMM4 の音声エフェクト「AIノイズ除去（RE-USE）」と同じにして、同じ入力から同じ出力を得る。
    /// 公式は最後のチャンクを末尾に揃えるが、ここでは最後のチャンクは入力がある分だけを処理する（YMM4 と同じ）。
    /// 入力を前から読みながら出力を書くので、長い音声でもメモリーに全部を載せない。
    /// </summary>
    internal sealed class ChunkedEnhancer(ISpeechEnhancer enhancer)
    {
        sealed record Chunk(float[][] Wet, float[][] Dry)
        {
            public int Length => Dry[0].Length;
        }

        /// <param name="strength">補正の強さ（0〜1）。元の音と補正した音を混ぜる割合</param>
        /// <param name="progress">書き出したフレーム数を受け取る</param>
        public void Process(IAudioReader input, IAudioWriter output, float strength, Action<long>? progress, CancellationToken token)
        {
            var channels = input.Channels;
            var sampleRate = input.SampleRate;
            var chunkLength = enhancer.GetChunkSamples(sampleRate);
            var hop = chunkLength / 2;
            // torch.hann_window（periodic）を 1e-7 で下から抑えたもの（公式と同じ）。端でも割り算が 0 にならない
            var window = new float[chunkLength];
            for (var i = 0; i < chunkLength; i++)
                window[i] = (float)Math.Max(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / chunkLength), 1e-7);

            var block = ReadBlock(input, hop, channels);
            var nextBlock = ReadBlock(input, hop, channels);
            var current = Start(block, nextBlock, channels, sampleRate, token);
            Chunk? previous = null;
            var written = 0L;
            var frame = new float[hop * channels];
            Task<Chunk>? next = null;
            try
            {
                while (current is not null)
                {
                    // チャンク k を待つ間に、チャンク k+1 の推論を始めておく（CPU の前処理・後処理を、もう片方の GPU の処理と重ねる）
                    block = nextBlock;
                    nextBlock = ReadBlock(input, hop, channels);
                    next = Start(block, nextBlock, channels, sampleRate, token);
                    var chunk = current.GetAwaiter().GetResult();
                    Mix(chunk, previous, hop, channels, window, strength, frame, output);
                    var run = Math.Min(hop, chunk.Length);
                    written += run;
                    progress?.Invoke(written);

                    previous = chunk;
                    current = next;
                    next = null;
                }
            }
            catch
            {
                // 走っている推論が終わるのを待ってから伝える。呼び出し元が推論器を破棄した後で GPU を使わないようにする
                WaitQuietly(current);
                WaitQuietly(next);
                throw;
            }
        }

        static void WaitQuietly(Task? task)
        {
            try
            {
                task?.Wait();
            }
            catch (AggregateException)
            {
                // 先に起きた例外を伝える
            }
        }

        /// <summary>チャンクの最初の hop フレームのうち入力がある分を、前のチャンクの後半と混ぜて書く</summary>
        static void Mix(Chunk chunk, Chunk? previous, int hop, int channels, float[] window, float strength, float[] frame, IAudioWriter output)
        {
            var run = Math.Min(hop, chunk.Length);
            for (var a = 0; a < run; a++)
            {
                for (var c = 0; c < channels; c++)
                {
                    float wet;
                    if (previous is not null)
                    {
                        var b = a + hop;
                        var wa = window[a];
                        var wb = window[b];
                        wet = (wa * chunk.Wet[c][a] + wb * previous.Wet[c][b]) / (wa + wb);
                    }
                    else
                    {
                        wet = chunk.Wet[c][a];
                    }
                    frame[a * channels + c] = chunk.Dry[c][a] * (1 - strength) + wet * strength;
                }
            }
            output.Write(frame.AsSpan(0, run * channels));
        }

        /// <summary>
        /// hop フレーム分の入力（チャンネル交互）。入力が尽きた後は空、尽きる途中なら短くなる
        /// </summary>
        static float[] ReadBlock(IAudioReader input, int hop, int channels)
        {
            var buffer = new float[hop * channels];
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = input.Read(buffer.AsSpan(filled));
                if (read <= 0)
                    break;
                filled += read;
            }
            return filled == buffer.Length ? buffer : buffer[..(filled / channels * channels)];
        }

        /// <summary>
        /// 連続する 2 つのブロックをつないだチャンクの推論を始める。入力が尽きた後のチャンクなら null
        /// </summary>
        Task<Chunk>? Start(float[] first, float[] second, int channels, int sampleRate, CancellationToken token)
        {
            var length = (first.Length + second.Length) / channels;
            if (length == 0)
                return null;

            var dry = new float[channels][];
            for (var c = 0; c < channels; c++)
            {
                var samples = new float[length];
                for (var i = 0; i < length; i++)
                {
                    var index = i * channels + c;
                    samples[i] = index < first.Length ? first[index] : second[index - first.Length];
                }
                dry[c] = samples;
            }

            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                // 中身が同じチャンネル（モノラルをステレオにした素材など）は 1 回だけ処理する。違うものは並べて走らせる
                var tasks = new Task<float[]>[channels];
                for (var c = 0; c < channels; c++)
                {
                    var same = -1;
                    for (var j = 0; j < c && same < 0; j++)
                    {
                        if (dry[j].AsSpan().SequenceEqual(dry[c]))
                            same = j;
                    }
                    var samples = dry[c];
                    tasks[c] = same >= 0 ? tasks[same] : Task.Run(() => enhancer.Enhance(samples, sampleRate, token), token);
                }
                try
                {
                    Task.WaitAll(tasks);
                }
                catch (AggregateException e)
                {
                    // 複数のチャンネルが同時に失敗・中止すると中身が複数になる。中止を優先し、呼び出し元が中止として扱えるようにする
                    var inner = e.InnerExceptions.FirstOrDefault(x => x is OperationCanceledException) ?? e.InnerExceptions[0];
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
                }
                var wet = new float[channels][];
                for (var c = 0; c < channels; c++)
                    wet[c] = tasks[c].Result;
                return new Chunk(wet, dry);
            }, token);
        }
    }
}

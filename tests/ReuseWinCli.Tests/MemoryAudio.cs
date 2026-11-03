using System;
using System.Collections.Generic;
using ReuseWinCli.Audio;

namespace ReuseWinCli.Tests
{
    /// <summary>配列から読む。maxRead を指定すると 1 回に返す数を抑え、チャンネルの区切りに揃わない読み方を再現する</summary>
    sealed class MemoryReader(float[] samples, int sampleRate, int channels, int maxRead = int.MaxValue) : IAudioReader
    {
        int position;

        public int SampleRate => sampleRate;
        public int Channels => channels;
        public long? EstimatedFrames => samples.Length / channels;

        public int Read(Span<float> destination)
        {
            var count = Math.Min(Math.Min(destination.Length, maxRead), samples.Length - position);
            samples.AsSpan(position, count).CopyTo(destination);
            position += count;
            return count;
        }
    }

    sealed class MemoryWriter : IAudioWriter
    {
        readonly List<float> samples = [];

        public void Write(ReadOnlySpan<float> data)
        {
            foreach (var value in data)
                samples.Add(value);
        }

        public float[] ToArray() => [.. samples];
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ReuseWinCli.Audio
{
    /// <summary>ヘッダーの無い 32bit float（リトルエンディアン、チャンネル交互）を書く</summary>
    internal sealed class RawWriter(Stream stream) : IAudioOutput
    {
        readonly bool streaming = !stream.CanSeek;

        public void Write(ReadOnlySpan<float> samples)
        {
            stream.Write(MemoryMarshal.AsBytes(samples));
            // パイプの先へ、チャンクを書くたびに届ける
            if (streaming)
                stream.Flush();
        }

        public void Dispose() => stream.Dispose();
    }
}

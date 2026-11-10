using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ReuseWinCli.Audio
{
    /// <summary>ヘッダーの無い 32bit float（リトルエンディアン、インターリーブ）を書き込む</summary>
    internal sealed class RawWriter(Stream stream) : IAudioOutput
    {
        readonly bool streaming = !stream.CanSeek;

        public void Write(ReadOnlySpan<float> samples)
        {
            stream.Write(MemoryMarshal.AsBytes(samples));
            // パイプの後段にすぐ渡るよう、チャンクを書き込むたびにフラッシュする
            if (streaming)
                stream.Flush();
        }

        public void Dispose() => stream.Dispose();
    }
}

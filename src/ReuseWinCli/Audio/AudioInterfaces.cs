using System;

namespace ReuseWinCli.Audio
{
    /// <summary>float のサンプルをチャンネル交互に読む</summary>
    internal interface IAudioReader
    {
        int SampleRate { get; }
        int Channels { get; }

        /// <summary>全体のフレーム数の見込み（進捗の表示用）。分からなければ null</summary>
        long? EstimatedFrames { get; }

        /// <summary>読んだサンプル数（フレーム数 × チャンネル数）を返す。終わりなら 0</summary>
        int Read(Span<float> destination);
    }

    /// <summary>float のサンプルをチャンネル交互に書く</summary>
    internal interface IAudioWriter
    {
        void Write(ReadOnlySpan<float> samples);
    }

    /// <summary>破棄で書き終える（ヘッダーの書き戻しとストリームを閉じる）出力</summary>
    internal interface IAudioOutput : IAudioWriter, IDisposable
    {
    }
}

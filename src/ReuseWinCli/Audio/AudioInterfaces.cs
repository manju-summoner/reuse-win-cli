using System;

namespace ReuseWinCli.Audio
{
    /// <summary>float のサンプルをインターリーブで読み込む</summary>
    internal interface IAudioReader
    {
        int SampleRate { get; }
        int Channels { get; }

        /// <summary>全体のフレーム数の推定値（進捗の表示用）。分からなければ null</summary>
        long? EstimatedFrames { get; }

        /// <summary>読み込んだサンプル数（フレーム数 × チャンネル数）を返す。終端なら 0</summary>
        int Read(Span<float> destination);
    }

    /// <summary>float のサンプルをインターリーブで書き込む</summary>
    internal interface IAudioWriter
    {
        void Write(ReadOnlySpan<float> samples);
    }

    /// <summary>Dispose で書き込みを完了する（ヘッダーを書き戻してストリームを閉じる）出力</summary>
    internal interface IAudioOutput : IAudioWriter, IDisposable
    {
    }
}

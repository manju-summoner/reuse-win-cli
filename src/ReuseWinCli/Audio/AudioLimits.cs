namespace ReuseWinCli.Audio
{
    internal static class AudioLimits
    {
        /// <summary>RE-USE の公式実装が対応するサンプリングレートの範囲（8kHz〜48kHz）</summary>
        public const int MinSampleRate = 8000;
        public const int MaxSampleRate = 48000;

        /// <summary>チャンネル数の上限。チャンクのバッファはチャンネル数に比例するので、オーバーフローや巨大なメモリー確保を起こす値をエラーにする（7.1.4 などは収まる）</summary>
        public const int MaxChannels = 32;
    }
}

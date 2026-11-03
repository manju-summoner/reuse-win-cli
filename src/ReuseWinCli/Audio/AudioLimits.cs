namespace ReuseWinCli.Audio
{
    internal static class AudioLimits
    {
        /// <summary>RE-USE の公式が受け付けるサンプリングレートの範囲（8kHz〜48kHz）</summary>
        public const int MinSampleRate = 8000;
        public const int MaxSampleRate = 48000;

        /// <summary>チャンネル数の上限。チャンクのバッファーはチャンネル数に比例するので、桁あふれや巨大な確保を起こす値を断る（7.1.4 などは収まる）</summary>
        public const int MaxChannels = 32;
    }
}

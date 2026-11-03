using System.Runtime.InteropServices;

namespace ReuseWinCli
{
    /// <summary>
    /// 画面に出す文言。Windows の表示言語が日本語なら日本語、それ以外は英語にする。
    /// InvariantGlobalization を有効にしているので CultureInfo からは表示言語が取れず、Win32 の API で調べる
    /// </summary>
    internal static partial class Messages
    {
        const int LangJapanese = 0x11;

        public static bool IsJapanese { get; } = (GetUserDefaultUILanguage() & 0x3ff) == LangJapanese;

        [LibraryImport("kernel32.dll")]
        private static partial ushort GetUserDefaultUILanguage();

        static string T(string ja, string en) => IsJapanese ? ja : en;

        public static string Banner(string version) => T(
            $"reuse-win-cli {version} — RE-USE（NVIDIA NemotronLabs）による音声のノイズ除去。非公式のツールで、NVIDIA とは関係ありません。",
            $"reuse-win-cli {version} — speech enhancement with RE-USE (NVIDIA NemotronLabs). Unofficial; not affiliated with or endorsed by NVIDIA.");

        public static string Usage => T(
            """
            使い方:
              reuse-win-cli <入力ファイル>... [オプション]
              または、音声・動画ファイルをこの exe にドラッグ＆ドロップしてください。

            入力と同じフォルダに「<元の名前>_enhanced.wav」を書き出します。
            読める形式: wav・mp3・m4a・aac・mp4・mov・wma・flac など（Windows が読めるもの）

            オプション:
              -o, --output <パス>      出力先のファイル（入力が 1 つのときだけ）
              -s, --strength <0-100>   補正の強さ（%）。元の音と混ぜる割合。既定は 100
              -y, --overwrite          出力先に同じ名前のファイルがあれば上書きする
                                       （指定しなければ「_enhanced (2).wav」のように番号を付ける。
                                        -o で指定した名前ならエラーにする）
                  --cpu                GPU を使わずに処理する（遅くなります）
                  --model <パス>       exe に入っている重みの代わりに使う RE-USE の重み（.safetensors）
                  --license            ライセンスを表示する
              -v, --version            バージョンを表示する
              -h, --help               この説明を表示する
            """,
            """
            Usage:
              reuse-win-cli <input file>... [options]
              Or drag and drop audio/video files onto this exe.

            Writes "<original name>_enhanced.wav" next to each input.
            Supported inputs: wav, mp3, m4a, aac, mp4, mov, wma, flac, etc. (anything Windows can decode)

            Options:
              -o, --output <path>      Output file (only with a single input)
              -s, --strength <0-100>   Enhancement strength in percent (mix with the original). Default: 100
              -y, --overwrite          Overwrite an existing output file
                                       (otherwise a number is appended, e.g. "_enhanced (2).wav";
                                        an explicit -o path is an error)
                  --cpu                Process without the GPU (slow)
                  --model <path>       Use these RE-USE weights (.safetensors) instead of the built-in ones
                  --license            Show license information
              -v, --version            Show the version
              -h, --help               Show this help
            """);

        public static string DropHint => T(
            "音声・動画ファイルを、この exe のアイコンにドラッグ＆ドロップしてください。",
            "Drag and drop audio/video files onto this exe's icon.");

        public static string PressAnyKey => T("何かキーを押すと閉じます。", "Press any key to close.");

        public static string UnknownOption(string option) => T($"不明なオプションです: {option}", $"Unknown option: {option}");

        public static string MissingValue(string option) => T($"{option} の値がありません。", $"Missing value for {option}.");

        public static string InvalidStrength(string value) => T(
            $"強さは 0〜100 の数で指定してください: {value}",
            $"Strength must be a number from 0 to 100: {value}");

        public static string OutputNeedsSingleInput => T(
            "--output は入力が 1 つのときだけ指定できます。",
            "--output can only be used with a single input.");

        public static string NoInput => T("入力ファイルを指定してください。", "Specify at least one input file.");

        public static string InputNotFound(string path) => T($"ファイルが見つかりません: {path}", $"File not found: {path}");

        public static string LoadingModel => T("モデルを準備しています…", "Preparing the model…");

        public static string ModelLoadFailed(string message) => T(
            $"モデルを読み込めませんでした: {message}",
            $"Failed to load the model: {message}");

        public static string UsingGpu(string adapter) => T($"GPU: {adapter}", $"GPU: {adapter}");

        public static string UsingWarp => T(
            "GPU を使わずに処理します（時間がかかります）。",
            "Processing without the GPU (this will be slow).");

        public static string FileHeader(int index, int count, string input) => T(
            $"[{index}/{count}] {input}",
            $"[{index}/{count}] {input}");

        public static string Format(int sampleRate, int channels, int? sourceRate) => sourceRate is { } source && source != sampleRate
            ? T($"  {source}Hz → {sampleRate}Hz に変換、{channels}ch", $"  {source}Hz resampled to {sampleRate}Hz, {channels}ch")
            : T($"  {sampleRate}Hz、{channels}ch", $"  {sampleRate}Hz, {channels}ch");

        public static string Progress(double percent, double done, double total) => T(
            $"  {percent,3:0}%（{done:0.0} / {total:0.0} 秒）",
            $"  {percent,3:0}% ({done:0.0} / {total:0.0} s)");

        public static string ProgressUnknown(double done) => T($"  {done:0.0} 秒", $"  {done:0.0} s");

        public static string Done(string output, double seconds) => T(
            $"  完了（{seconds:0.0} 秒）: {output}",
            $"  Done ({seconds:0.0} s): {output}");

        public static string Failed(string message) => T($"  失敗しました: {message}", $"  Failed: {message}");

        public static string Cancelled => T("  中止しました。", "  Cancelled.");

        public static string MediaFoundationMissing => T(
            "Windows の Media Foundation が見つかりません。Windows の N エディションでは、「メディア機能パック」（Media Feature Pack）を入れてください。",
            "Windows Media Foundation was not found. On Windows N editions, install the Media Feature Pack.");

        public static string OutputIsDirectory(string path) => T(
            $"出力先にはファイルの名前を指定してください（フォルダが指定されています）: {path}",
            $"The output must be a file name, not a folder: {path}");

        public static string OutputExists(string path) => T(
            $"出力先のファイルが既にあります（上書きするときは -y を付けてください）: {path}",
            $"The output file already exists (use -y to overwrite): {path}");

        public static string UnsupportedFile => T(
            "このファイルは読めません（Windows が対応していない形式か、壊れています）。",
            "Cannot read this file (the format is not supported by Windows, or the file is broken).");

        public static string NoAudioTrack => T("音声のトラックがありません。", "The file has no audio track.");

        public static string CannotConvertToFloat => T("音声を 32bit float に変換できません。", "Cannot convert the audio to 32-bit float.");

        public static string UnsupportedSampleRate(int rate) => T(
            $"サンプリングレート {rate}Hz には対応していません。",
            $"Unsupported sample rate: {rate} Hz.");

        public static string UnknownChannels => T("音声のチャンネル数が分かりません。", "Unknown number of audio channels.");

        public static string ReadFailed => T("音声の読み込みに失敗しました。", "Failed to read the audio.");

        public static string FormatChanged => T(
            "途中で音声の形式が変わるファイルには対応していません。",
            "Files whose audio format changes midway are not supported.");

        public static string WavTooLarge => T(
            "出力が WAV の上限（4GB）を超えるので書き出せません。",
            "The output exceeds the WAV size limit (4 GB).");

        public static string Summary(int succeeded, int failed) => failed == 0
            ? T($"{succeeded} 件を処理しました。", $"Processed {succeeded} file(s).")
            : T($"{succeeded} 件を処理し、{failed} 件が失敗しました。", $"Processed {succeeded} file(s); {failed} failed.");
    }
}

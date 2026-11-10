using System.Runtime.InteropServices;

namespace ReuseWinCli
{
    /// <summary>
    /// コンソールに表示するメッセージ。Windows の表示言語が日本語なら日本語、それ以外は英語にする。
    /// InvariantGlobalization を有効にしているので CultureInfo からは表示言語を取得できず、Win32 API で取得する
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

            入力と同じフォルダに「<元のファイル名>_enhanced.wav」（--out-format f32le の場合は .raw）を出力します。
            入力できる形式: wav・mp3・m4a・aac・mp4・mov・wma・flac など（Windows がデコードできるもの）

            オプション:
              -o, --output <パス>      出力先のファイル（入力が 1 つの場合のみ）。「-」で標準出力
              -s, --strength <0-100>   補正の強さ（%）。元の音声と混合する割合。既定値は 100
              -y, --overwrite          出力先に同名のファイルがあれば上書きする
                                       （指定しない場合は「_enhanced (2).wav」のように連番を付与する。
                                        -o で指定したファイル名ならエラーにする）
                  --cpu                GPU を使わずに処理する（遅くなります）
                  --model <パス>       exe に同梱されている重みの代わりに使用する RE-USE の重み（.safetensors）
                  --license            ライセンスを表示する
              -v, --version            バージョンを表示する
              -h, --help               このヘルプを表示する

            標準入出力（入力に「-」を指定すると標準入力から読み込み、出力は既定で標準出力）:
                  --in-format <形式>   標準入力の形式。wav（既定）・f32le・s16le
                  --rate <Hz>          標準入力が f32le・s16le の場合のサンプリングレート（8000〜48000）
                  --channels <数>      標準入力が f32le・s16le の場合のチャンネル数（1〜32）
                  --out-format <形式>  出力の形式。wav（既定）・f32le
              例: ffmpeg -i in.mp4 -f f32le -ac 2 -ar 48000 - | reuse-win-cli - --in-format f32le --rate 48000 --channels 2 -o out.wav
              （Windows PowerShell 5.1 はパイプのバイナリを壊すので、cmd.exe か PowerShell 7.4 以降で実行してください）
            """,
            """
            Usage:
              reuse-win-cli <input file>... [options]
              Or drag and drop audio/video files onto this exe.

            Writes "<original name>_enhanced.wav" (.raw with --out-format f32le) next to each input.
            Supported inputs: wav, mp3, m4a, aac, mp4, mov, wma, flac, etc. (anything Windows can decode)

            Options:
              -o, --output <path>      Output file (only with a single input). "-" for standard output
              -s, --strength <0-100>   Enhancement strength in percent (mix with the original). Default: 100
              -y, --overwrite          Overwrite an existing output file
                                       (otherwise a number is appended, e.g. "_enhanced (2).wav";
                                        an explicit -o path is an error)
                  --cpu                Process without the GPU (slow)
                  --model <path>       Use these RE-USE weights (.safetensors) instead of the built-in ones
                  --license            Show license information
              -v, --version            Show the version
              -h, --help               Show this help

            Standard input/output ("-" as the input reads standard input; the output defaults to standard output):
                  --in-format <fmt>    Format of standard input: wav (default), f32le, s16le
                  --rate <Hz>          Sample rate for f32le/s16le input (8000-48000)
                  --channels <n>       Channel count for f32le/s16le input (1-32)
                  --out-format <fmt>   Output format: wav (default), f32le
              Example: ffmpeg -i in.mp4 -f f32le -ac 2 -ar 48000 - | reuse-win-cli - --in-format f32le --rate 48000 --channels 2 -o out.wav
              (Windows PowerShell 5.1 corrupts binary pipes; use cmd.exe or PowerShell 7.4+.)
            """);

        public static string DropHint => T(
            "音声・動画ファイルを、この exe のアイコンにドラッグ＆ドロップしてください。",
            "Drag and drop audio/video files onto this exe's icon.");

        public static string PressAnyKey => T("何かキーを押すと閉じます。", "Press any key to close.");

        public static string UnknownOption(string option) => T($"不明なオプションです: {option}", $"Unknown option: {option}");

        public static string MissingValue(string option) => T($"{option} の値がありません。", $"Missing value for {option}.");

        public static string InvalidStrength(string value) => T(
            $"強さは 0〜100 の数値で指定してください: {value}",
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

        public static string StandardInput => T("標準入力", "standard input");

        public static string StandardOutput => T("標準出力", "standard output");

        public static string StdinMustBeOnlyInput => T(
            "標準入力（-）は、ほかの入力と一緒には指定できません。",
            "Standard input (-) cannot be combined with other inputs.");

        public static string InvalidChoice(string option, string value, string choices) => T(
            $"{option} には {choices} のどれかを指定してください: {value}",
            $"{option} must be one of {choices}: {value}");

        public static string InvalidNumber(string option, string value, int min, int max) => T(
            $"{option} には {min}〜{max} の整数を指定してください: {value}",
            $"{option} must be an integer from {min} to {max}: {value}");

        public static string UnsupportedChannels(int channels) => T(
            $"チャンネル数 {channels} には対応していません（1〜{Audio.AudioLimits.MaxChannels}）。",
            $"Unsupported channel count: {channels} (1-{Audio.AudioLimits.MaxChannels}).");

        public static string StdoutIsTerminal => T(
            "標準出力がコンソールに接続されています。音声を標準出力へ出力する場合は、パイプかファイルにリダイレクトしてください（ファイルに保存する場合は -o <ファイル> を指定してください）。",
            "Standard output is a terminal. Redirect it to a pipe or file when writing audio to standard output (or use -o <file>).");

        public static string StdinIsTerminal => T(
            "標準入力がコンソールに接続されています。入力に「-」を指定する場合は、パイプかファイルからリダイレクトしてください。",
            "Standard input is a terminal. Redirect a pipe or file into it when using \"-\" as the input.");

        public static string RawNeedsRateAndChannels => T(
            "--rate と --channels は、--in-format が f32le・s16le の場合に両方を指定してください（wav の場合は指定できません）。",
            "Specify both --rate and --channels when --in-format is f32le or s16le (they cannot be used with wav).");

        public static string StdinOptionsNeedStdin => T(
            "--in-format・--rate・--channels は、入力が標準入力（-）の場合のみ指定できます。",
            "--in-format, --rate and --channels can only be used when the input is standard input (-).");

        public static string NotWav => T(
            "WAV として読み込めません（f32le・s16le の場合は --in-format を指定してください）。",
            "Cannot read the input as WAV (use --in-format for f32le or s16le).");

        public static string UnsupportedWavFormat(int tag, int bits) => T(
            $"この WAV の形式には対応していません（形式 {tag}・{bits}bit）。16/24/32bit の PCM と 32bit float に対応しています。",
            $"Unsupported WAV format (format {tag}, {bits}-bit). 16/24/32-bit PCM and 32-bit float are supported.");

        public static string OutputClosed => T(
            "  出力先が閉じられたので終了します。",
            "  The output was closed; stopping.");

        public static string MediaFoundationMissing => T(
            "Windows の Media Foundation が見つかりません。Windows の N エディションでは、「メディア機能パック」（Media Feature Pack）をインストールしてください。",
            "Windows Media Foundation was not found. On Windows N editions, install the Media Feature Pack.");

        public static string OutputIsDirectory(string path) => T(
            $"出力先にはファイル名を指定してください（フォルダが指定されています）: {path}",
            $"The output must be a file name, not a folder: {path}");

        public static string OutputExists(string path) => T(
            $"出力先のファイルが既にあります（上書きするときは -y を付けてください）: {path}",
            $"The output file already exists (use -y to overwrite): {path}");

        public static string UnsupportedFile => T(
            "このファイルは読み込めません（Windows が対応していない形式か、ファイルが破損しています）。",
            "Cannot read this file (the format is not supported by Windows, or the file is broken).");

        public static string NoAudioTrack => T("音声トラックがありません。", "The file has no audio track.");

        public static string CannotConvertToFloat => T("音声を 32bit float に変換できません。", "Cannot convert the audio to 32-bit float.");

        public static string UnsupportedSampleRate(int rate) => T(
            $"サンプリングレート {rate}Hz には対応していません。",
            $"Unsupported sample rate: {rate} Hz.");

        public static string UnknownChannels => T("音声のチャンネル数が不明です。", "Unknown number of audio channels.");

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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ReuseWinCli.Audio;
using ReuseWinCli.Enhancement;
using Vortice.MediaFoundation;

namespace ReuseWinCli
{
    internal static partial class Program
    {
        const int ExitSuccess = 0;
        const int ExitFailed = 1;
        const int ExitUsage = 2;

        /// <summary>入力・出力に指定すると標準入力・標準出力を表す</summary>
        internal const string StandardStream = "-";

        internal sealed class Options
        {
            public List<string> Inputs { get; } = [];
            public string? Output { get; set; }
            public float Strength { get; set; } = 1;
            public bool Overwrite { get; set; }
            public bool ForceWarp { get; set; }
            public string? ModelPath { get; set; }
            /// <summary>標準入力の形式（wav・f32le・s16le）</summary>
            public string InFormat { get; set; } = "wav";
            /// <summary>出力の形式（wav・f32le）</summary>
            public string OutFormat { get; set; } = "wav";
            public int? Rate { get; set; }
            public int? Channels { get; set; }

            public bool ReadsStandardInput => Inputs is [StandardStream];
            public bool WritesStandardOutput => Output == StandardStream;
        }

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // エクスプローラーからの起動（ドラッグ＆ドロップ・ダブルクリック）では、このプロセスのためにコンソールが作られ、終わると閉じてしまう。
            // 結果を読めるよう、そのときだけ最後にキーを待つ
            var ownsConsole = OwnsConsole();
            try
            {
                return Run(args, ownsConsole);
            }
            finally
            {
                if (ownsConsole)
                {
                    Console.WriteLine();
                    Console.WriteLine(Messages.PressAnyKey);
                    Console.ReadKey(true);
                }
            }
        }

        static int Run(string[] args, bool ownsConsole)
        {
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
            if (args.Length == 0)
            {
                Console.WriteLine(Messages.Banner(version));
                Console.WriteLine();
                Console.WriteLine(ownsConsole ? Messages.DropHint : Messages.Usage);
                return ownsConsole ? ExitSuccess : ExitUsage;
            }

            Options options;
            try
            {
                var parsed = Parse(args, version);
                if (parsed is null)
                    return ExitSuccess;
                options = parsed;
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine();
                Console.Error.WriteLine(Messages.Usage);
                return ExitUsage;
            }

            // 音声のバイナリを画面に出したり、キーボードからの入力を待ったりしないよう、標準入出力はリダイレクトされているときだけ使う
            if (options.WritesStandardOutput && !Console.IsOutputRedirected || options.ReadsStandardInput && !Console.IsInputRedirected)
            {
                Console.Error.WriteLine(options.WritesStandardOutput && !Console.IsOutputRedirected ? Messages.StdoutIsTerminal : Messages.StdinIsTerminal);
                return ExitUsage;
            }

            // 標準出力に音声を流すときは、画面への表示がデータに混ざらないよう、すべて標準エラーへ出す
            if (options.WritesStandardOutput)
                Console.SetOut(Console.Error);
            Console.WriteLine(Messages.Banner(version));
            using var cancellation = new CancellationTokenSource();
            void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
            {
                // 1 回目は書きかけのファイルを片付けてから終わる。走っている推論は止められないので、待てないときのために 2 回目はそのまま終わらせる
                if (cancellation.IsCancellationRequested)
                    return;
                e.Cancel = true;
                cancellation.Cancel();
            }
            Console.CancelKeyPress += OnCancelKeyPress;
            try
            {
                return RunFiles(options, cancellation);
            }
            finally
            {
                // 最後のキー待ちの間の Ctrl+C で、破棄した CancellationTokenSource に触らないようにする
                Console.CancelKeyPress -= OnCancelKeyPress;
            }
        }

        static int RunFiles(Options options, CancellationTokenSource cancellation)
        {
            // 標準入力は自前で読むので、Media Foundation はファイルを読むときだけ使う
            var usesMediaFoundation = !options.ReadsStandardInput;
            if (usesMediaFoundation)
            {
                try
                {
                    MediaFactory.MFStartup().CheckError();
                }
                catch (DllNotFoundException)
                {
                    // Windows の N エディションには Media Foundation が入っていない
                    Console.Error.WriteLine(Messages.MediaFoundationMissing);
                    return ExitFailed;
                }
            }
            try
            {
                Console.WriteLine(Messages.LoadingModel);
                ReUseEngine engine;
                try
                {
                    engine = ReUseEngine.Create(options.ModelPath, options.ForceWarp);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Console.Error.WriteLine(Messages.ModelLoadFailed(e.Message));
                    return ExitFailed;
                }
                using (engine)
                {
                    Console.WriteLine(engine.IsWarp ? Messages.UsingWarp : Messages.UsingGpu(engine.AdapterName));
                    var succeeded = 0;
                    var failed = 0;
                    for (var i = 0; i < options.Inputs.Count; i++)
                    {
                        if (cancellation.IsCancellationRequested)
                            break;
                        Console.WriteLine();
                        Console.WriteLine(Messages.FileHeader(i + 1, options.Inputs.Count, options.ReadsStandardInput ? Messages.StandardInput : options.Inputs[i]));
                        if (ProcessFile(engine, options, options.Inputs[i], cancellation.Token))
                            succeeded++;
                        else
                            failed++;
                    }
                    Console.WriteLine();
                    Console.WriteLine(Messages.Summary(succeeded, failed));
                    return failed == 0 && !cancellation.IsCancellationRequested ? ExitSuccess : ExitFailed;
                }
            }
            finally
            {
                if (usesMediaFoundation)
                    MediaFactory.MFShutdown();
            }
        }

        /// <returns>処理を続けないとき（ヘルプ・バージョン・ライセンスを表示したとき）は null</returns>
        internal static Options? Parse(string[] args, string version)
        {
            var options = new Options();
            var inFormatSpecified = false;
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                string Value()
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException(Messages.MissingValue(arg));
                    return args[++i];
                }

                switch (arg)
                {
                    case "-h" or "--help" or "/?":
                        Console.WriteLine(Messages.Banner(version));
                        Console.WriteLine();
                        Console.WriteLine(Messages.Usage);
                        return null;
                    case "-v" or "--version":
                        Console.WriteLine(version);
                        return null;
                    case "--license":
                        Console.WriteLine(EmbeddedResources.ReadText("LICENSE"));
                        Console.WriteLine();
                        Console.WriteLine(EmbeddedResources.ReadText("THIRD-PARTY-NOTICES.txt"));
                        return null;
                    case "-o" or "--output":
                        options.Output = Value();
                        break;
                    case "-s" or "--strength":
                        var text = Value();
                        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || !(percent is >= 0 and <= 100))
                            throw new ArgumentException(Messages.InvalidStrength(text));
                        options.Strength = (float)(percent / 100);
                        break;
                    case "-y" or "--overwrite":
                        options.Overwrite = true;
                        break;
                    case "--cpu":
                        options.ForceWarp = true;
                        break;
                    case "--model":
                        options.ModelPath = Value();
                        break;
                    case "--in-format":
                        options.InFormat = Choice(arg, Value(), "wav", "f32le", "s16le");
                        inFormatSpecified = true;
                        break;
                    case "--out-format":
                        options.OutFormat = Choice(arg, Value(), "wav", "f32le");
                        break;
                    case "--rate":
                        options.Rate = IntegerInRange(arg, Value(), AudioLimits.MinSampleRate, AudioLimits.MaxSampleRate);
                        break;
                    case "--channels":
                        options.Channels = IntegerInRange(arg, Value(), 1, AudioLimits.MaxChannels);
                        break;
                    default:
                        if (arg.StartsWith('-') && arg.Length > 1)
                            throw new ArgumentException(Messages.UnknownOption(arg));
                        options.Inputs.Add(arg);
                        break;
                }
            }

            if (options.Inputs.Count == 0)
                throw new ArgumentException(Messages.NoInput);
            if (options.Inputs.Contains(StandardStream) && options.Inputs.Count > 1)
                throw new ArgumentException(Messages.StdinMustBeOnlyInput);
            if (options.Output is not null && options.Inputs.Count > 1)
                throw new ArgumentException(Messages.OutputNeedsSingleInput);
            var stdinOptions = inFormatSpecified || options.Rate is not null || options.Channels is not null;
            if (stdinOptions && !options.ReadsStandardInput)
                throw new ArgumentException(Messages.StdinOptionsNeedStdin);
            // WAV はヘッダーにサンプリングレートとチャンネル数があり、raw には無い
            if ((options.InFormat == "wav") != (options.Rate is null && options.Channels is null)
                || (options.Rate is null) != (options.Channels is null))
                throw new ArgumentException(Messages.RawNeedsRateAndChannels);
            // 標準入力には名前が無いので、出力先を省いたら標準出力へ流す
            if (options.ReadsStandardInput && options.Output is null)
                options.Output = StandardStream;
            return options;
        }

        static string Choice(string option, string value, params string[] choices)
            => Array.IndexOf(choices, value) >= 0 ? value : throw new ArgumentException(Messages.InvalidChoice(option, value, string.Join(" / ", choices)));

        static int IntegerInRange(string option, string value, int min, int max)
            => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max
                ? number
                : throw new ArgumentException(Messages.InvalidNumber(option, value, min, max));

        static bool ProcessFile(ReUseEngine engine, Options options, string input, CancellationToken token)
        {
            if (!options.ReadsStandardInput && !File.Exists(input))
            {
                Console.Error.WriteLine(Messages.Failed(Messages.InputNotFound(input)));
                return false;
            }
            if (options.WritesStandardOutput)
                return ProcessToStandardOutput(engine, options, input, token);

            var output = options.Output ?? GetDefaultOutputPath(input, options.Overwrite, OutputExtension(options));
            if (CheckOutputPath(output, options.Overwrite) is { } problem)
            {
                Console.Error.WriteLine(Messages.Failed(problem));
                return false;
            }
            // 途中で失敗・中止したときに、壊れた出力や前の出力を残さないよう、別の名前で書いてから置き換える。
            // 同じ出力先へ同時に書くほかのプロセスのファイルを壊さないよう、名前は毎回変え、自分で作ったものだけを消す。
            // 出力の名前に足すと、出力なら作れる長い名前でもファイル名の上限（255 文字）を超えるので、出力の名前とは関係なく決める
            var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $"reuse-win-cli.{Guid.NewGuid().ToString("N")[..8]}.partial");
            var created = false;
            var watch = Stopwatch.StartNew();
            try
            {
                var (reader, fileReader) = OpenReader(options, input, token);
                using (fileReader)
                {
                    Console.WriteLine(Messages.Format(reader.SampleRate, reader.Channels, fileReader?.SourceSampleRate));
                    // WAV の上限は書き込みで確かめる。長さの見込み（EstimatedFrames）は音声トラックではなくファイル全体の長さなので、先に判定すると収まる音声まで断ってしまう
                    var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite);
                    created = true;
                    using var writer = CreateWriter(options, file, reader.SampleRate, reader.Channels);
                    Enhance(engine, options, reader, writer, watch, token);
                }
                // 最後の推論の途中で中止されても、推論そのものは最後まで走って書き出される。中止したなら出力を確定させない
                token.ThrowIfCancellationRequested();
                File.Move(temporary, output, options.Overwrite);
                Console.WriteLine(Messages.Done(output, watch.Elapsed.TotalSeconds));
                return true;
            }
            // 標準入力の読み込みを待っている間の中止も、CancellableStream が OperationCanceledException にするのでここに来る
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (created)
                    TryDelete(temporary);
                Console.WriteLine();
                Console.Error.WriteLine(Messages.Cancelled);
                return false;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (created)
                    TryDelete(temporary);
                Console.WriteLine();
                Console.Error.WriteLine(Messages.Failed(e.Message));
                return false;
            }
        }

        /// <summary>
        /// 標準出力へ書く場合。書いたものは取り消せないので、一時ファイルは使わない。
        /// 出力先のプログラムが先に終わってパイプが閉じたら、失敗にせずに終える
        /// </summary>
        static bool ProcessToStandardOutput(ReUseEngine engine, Options options, string input, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var (reader, fileReader) = OpenReader(options, input, token);
                using (fileReader)
                {
                    Console.WriteLine(Messages.Format(reader.SampleRate, reader.Channels, fileReader?.SourceSampleRate));
                    // 読み手がパイプを開いたまま読まなくなっても、中止できるようにする。
                    // ファイルへのリダイレクト（シークできる）は書き込みが止まったままにならないので包まない。包むと中止の後に WAV のヘッダーを書き戻せない
                    Stream stdout = StandardStreams.OpenOutput();
                    if (!stdout.CanSeek)
                        stdout = new CancellableStream(stdout, token);
                    using var writer = CreateWriter(options, stdout, reader.SampleRate, reader.Channels);
                    Enhance(engine, options, reader, writer, watch, token);
                }
                token.ThrowIfCancellationRequested();
                Console.WriteLine(Messages.Done(Messages.StandardOutput, watch.Elapsed.TotalSeconds));
                return true;
            }
            // Ctrl+C はパイプの先のプログラムにも届くので、中止したときは先にパイプが閉じられることがある。そのときは中止として扱う
            catch (IOException e) when (IsBrokenPipe(e) && !token.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.Error.WriteLine(Messages.OutputClosed);
                return true;
            }
            catch (Exception e) when (token.IsCancellationRequested && (e is OperationCanceledException || e is IOException io && IsBrokenPipe(io)))
            {
                Console.WriteLine();
                Console.Error.WriteLine(Messages.Cancelled);
                return false;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine();
                Console.Error.WriteLine(Messages.Failed(e.Message));
                return false;
            }
        }

        /// <summary>入力を開く。ファイルは Media Foundation で、標準入力は自前で読む（パイプからは読めない形式があるため）</summary>
        /// <returns>FileReader は破棄が要る（標準入力のときは null）</returns>
        static (IAudioReader Reader, MediaFoundationReader? FileReader) OpenReader(Options options, string input, CancellationToken token)
        {
            if (!options.ReadsStandardInput)
            {
                var fileReader = new MediaFoundationReader(Path.GetFullPath(input));
                return (fileReader, fileReader);
            }
            // 入力元がパイプを開いたまま何も送らなくなっても、中止できるようにする
            var stdin = new CancellableStream(StandardStreams.OpenInput(), token);
            IAudioReader reader = options.InFormat switch
            {
                "f32le" => new PcmStreamReader(stdin, PcmSampleFormat.Float32, options.Rate!.Value, options.Channels!.Value),
                "s16le" => new PcmStreamReader(stdin, PcmSampleFormat.Int16, options.Rate!.Value, options.Channels!.Value),
                _ => PcmStreamReader.OpenWav(stdin),
            };
            return (reader, null);
        }

        /// <summary>パイプの読み手が終わった（ERROR_BROKEN_PIPE・ERROR_NO_DATA）</summary>
        static bool IsBrokenPipe(IOException e) => (e.HResult & 0xFFFF) is 109 or 232;

        static IAudioOutput CreateWriter(Options options, Stream stream, int sampleRate, int channels)
        {
            try
            {
                return options.OutFormat == "f32le" ? new RawWriter(stream) : new WavWriter(stream, sampleRate, channels);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        static string OutputExtension(Options options) => options.OutFormat == "f32le" ? ".raw" : ".wav";

        /// <summary>ChunkedEnhancer で補正しながら、進捗を表示する</summary>
        static void Enhance(ReUseEngine engine, Options options, IAudioReader reader, IAudioWriter writer, Stopwatch watch, CancellationToken token)
        {
            var lastReport = 0L;
            var lastFrames = 0L;
            var lastLine = "";
            // 表示は、標準出力へ音声を流すときは標準エラーに出している
            var interactive = !(options.WritesStandardOutput ? Console.IsErrorRedirected : Console.IsOutputRedirected);
            new ChunkedEnhancer(engine).Process(reader, writer, options.Strength, frames =>
            {
                lastFrames = frames;
                if (!interactive)
                    return;
                var now = watch.ElapsedMilliseconds;
                if (now - lastReport < 200)
                    return;
                lastReport = now;
                lastLine = FormatProgress(frames, reader);
                Console.Write('\r' + lastLine);
            }, token);
            // 同じ行に上書きするので、前の行より短いと末尾が残る。前の行の長さまで空白で埋める
            if (interactive)
                Console.WriteLine('\r' + FormatProgress(lastFrames, reader, finished: true).PadRight(lastLine.Length));
        }

        /// <param name="finished">書き終えたか。長さの見込みは実際の長さとずれることがあるので、終わったら実際の長さを全体とする</param>
        static string FormatProgress(long frames, IAudioReader reader, bool finished = false)
        {
            var done = (double)frames / reader.SampleRate;
            if (reader.EstimatedFrames is not { } total || total <= 0)
                return Messages.ProgressUnknown(done);
            if (finished)
                return Messages.Progress(100, done, done);
            var totalSeconds = (double)total / reader.SampleRate;
            return Messages.Progress(Math.Min(100, 100.0 * frames / total), Math.Min(done, totalSeconds), totalSeconds);
        }

        /// <summary>入力の隣の「<元の名前>_enhanced.wav」。上書きしないときは、空いている「_enhanced (n).wav」を探す</summary>
        /// <param name="extension">出力の形式の拡張子（f32le なら .raw）</param>
        internal static string GetDefaultOutputPath(string input, bool overwrite, string extension = ".wav")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(input))!;
            var name = Path.GetFileNameWithoutExtension(input) + "_enhanced";
            var path = Path.Combine(directory, name + extension);
            if (overwrite)
                return path;
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(directory, $"{name} ({n}){extension}");
            return path;
        }

        /// <summary>出力先に書けないときの理由。書けるなら null</summary>
        internal static string? CheckOutputPath(string output, bool overwrite)
        {
            if (Directory.Exists(output) || output.EndsWith(Path.DirectorySeparatorChar) || output.EndsWith(Path.AltDirectorySeparatorChar))
                return Messages.OutputIsDirectory(output);
            if (!overwrite && File.Exists(output))
                return Messages.OutputExists(output);
            return null;
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>このプロセスだけがコンソールにつながっているか（エクスプローラーから起動されたか）</summary>
        static bool OwnsConsole()
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                return false;
            Span<uint> processes = stackalloc uint[2];
            return GetConsoleProcessList(ref MemoryMarshal.GetReference(processes), (uint)processes.Length) == 1;
        }

        [LibraryImport("kernel32.dll")]
        private static partial uint GetConsoleProcessList(ref uint processList, uint processCount);
    }
}

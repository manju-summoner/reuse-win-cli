using System;
using NUnit.Framework;

namespace ReuseWinCli.Tests
{
    /// <summary>コマンドライン引数のパース（標準入出力のオプションの組み合わせ）</summary>
    class ProgramOptionsTest
    {
        static Program.Options Parse(params string[] args) => Program.Parse(args, "test")!;

        [Test]
        public void StandardInputDefaultsToStandardOutput()
        {
            var options = Parse("-");
            Assert.That(options.ReadsStandardInput, Is.True);
            Assert.That(options.WritesStandardOutput, Is.True);
            Assert.That(options.InFormat, Is.EqualTo("wav"));
            Assert.That(options.OutFormat, Is.EqualTo("wav"));
        }

        [Test]
        public void StandardInputCanWriteToFileAndFileCanWriteToStandardOutput()
        {
            var toFile = Parse("-", "-o", "out.wav");
            Assert.That(toFile.ReadsStandardInput, Is.True);
            Assert.That(toFile.WritesStandardOutput, Is.False);

            var fromFile = Parse("in.mp4", "-o", "-", "--out-format", "f32le");
            Assert.That(fromFile.ReadsStandardInput, Is.False);
            Assert.That(fromFile.WritesStandardOutput, Is.True);
            Assert.That(fromFile.OutFormat, Is.EqualTo("f32le"));
        }

        [Test]
        public void RawInputNeedsRateAndChannels()
        {
            var options = Parse("-", "--in-format", "s16le", "--rate", "44100", "--channels", "2");
            Assert.That((options.InFormat, options.Rate, options.Channels), Is.EqualTo(("s16le", (int?)44100, (int?)2)));

            AssertUsageError(Messages.RawNeedsRateAndChannels, "-", "--in-format", "f32le");
            AssertUsageError(Messages.RawNeedsRateAndChannels, "-", "--in-format", "f32le", "--rate", "48000");
            AssertUsageError(Messages.RawNeedsRateAndChannels, "-", "--rate", "48000", "--channels", "2");
        }

        [Test]
        public void RejectsInvalidCombinations()
        {
            AssertUsageError(Messages.StdinMustBeOnlyInput, "-", "a.wav");
            AssertUsageError(Messages.StdinOptionsNeedStdin, "a.wav", "--in-format", "f32le", "--rate", "48000", "--channels", "1");
            AssertUsageError(Messages.OutputNeedsSingleInput, "a.wav", "b.wav", "-o", "-");
            AssertUsageError(Messages.InvalidChoice("--in-format", "mp3", "wav / f32le / s16le"), "-", "--in-format", "mp3");
            AssertUsageError(Messages.InvalidChoice("--out-format", "s16le", "wav / f32le"), "a.wav", "--out-format", "s16le");
            AssertUsageError(Messages.InvalidNumber("--rate", "0", 8000, 48000), "-", "--in-format", "f32le", "--rate", "0", "--channels", "1");
            AssertUsageError(Messages.InvalidNumber("--rate", "96000", 8000, 48000), "-", "--in-format", "f32le", "--rate", "96000", "--channels", "1");
            AssertUsageError(Messages.InvalidNumber("--channels", "33", 1, 32), "-", "--in-format", "f32le", "--rate", "48000", "--channels", "33");
            // 既定値と同じ wav でも、ファイル入力には標準入力用のオプションを指定できない
            AssertUsageError(Messages.StdinOptionsNeedStdin, "a.wav", "--in-format", "wav");
        }

        [Test]
        public void DefaultOutputUsesExtensionOfFormat()
        {
            var directory = System.IO.Path.GetTempPath();
            Assert.That(Program.GetDefaultOutputPath(System.IO.Path.Combine(directory, "voice.mp3"), true, ".raw"), Is.EqualTo(System.IO.Path.Combine(directory, "voice_enhanced.raw")));
        }

        static void AssertUsageError(string message, params string[] args)
            => Assert.That(() => Program.Parse(args, "test"), Throws.ArgumentException.With.Message.EqualTo(message));
    }
}

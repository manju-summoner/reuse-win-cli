using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ReuseWinCli.Audio;
using Vortice.MediaFoundation;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// 書き出した WAV を Media Foundation で読み込み直して、内容と形式が保たれることを確認する（WAV の書き込みと、読み込みの両方の検証になる）
    /// </summary>
    class AudioFileTest
    {
        string directory = "";

        [OneTimeSetUp]
        public void OneTimeSetUp() => MediaFactory.MFStartup().CheckError();

        [OneTimeTearDown]
        public void OneTimeTearDown() => MediaFactory.MFShutdown();

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "reuse-win-cli-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(directory, true);

        [TestCase(44100, 1)]
        [TestCase(48000, 2)]
        [TestCase(16000, 3)]
        public void WavRoundTripsThroughMediaFoundation(int sampleRate, int channels)
        {
            var frames = sampleRate / 3 + 17;
            var samples = Enumerable.Range(0, frames * channels).Select(i => MathF.Sin(i * 0.01f) * 1.5f).ToArray();
            var path = Path.Combine(directory, "a.wav");
            using (var writer = new WavWriter(File.Create(path), sampleRate, channels))
            {
                writer.Write(samples.AsSpan(0, 1000 * channels));
                writer.Write(samples.AsSpan(1000 * channels));
            }

            using var reader = new MediaFoundationReader(path);
            Assert.That(reader.SampleRate, Is.EqualTo(sampleRate));
            Assert.That(reader.SourceSampleRate, Is.EqualTo(sampleRate));
            Assert.That(reader.Channels, Is.EqualTo(channels));
            Assert.That(reader.EstimatedFrames, Is.EqualTo(frames).Within(1));
            var read = new float[samples.Length + 100];
            var total = 0;
            int count;
            while ((count = reader.Read(read.AsSpan(total))) > 0)
                total += count;
            // 1 を超える値も丸めずに残る
            Assert.That(read[..total], Is.EqualTo(samples));
        }

        [Test]
        public void HighSampleRateIsConvertedIntoSupportedRange()
        {
            const int rate = 96000;
            var path = Path.Combine(directory, "high.wav");
            using (var writer = new WavWriter(File.Create(path), rate, 1))
                writer.Write(Enumerable.Range(0, rate).Select(i => MathF.Sin(i * 0.01f) * 0.5f).ToArray());

            using var reader = new MediaFoundationReader(path);
            Assert.That(reader.SourceSampleRate, Is.EqualTo(rate));
            Assert.That(reader.SampleRate, Is.EqualTo(48000));
            var buffer = new float[100000];
            var total = 0;
            int count;
            while ((count = reader.Read(buffer.AsSpan(total))) > 0)
                total += count;
            Assert.That(total, Is.EqualTo(48000).Within(100));
        }

        [Test]
        public void NonMediaFileIsRejectedWithReadableMessage()
        {
            var path = Path.Combine(directory, "text.wav");
            File.WriteAllText(path, "not audio");
            Assert.That(() => new MediaFoundationReader(path), Throws.InstanceOf<InvalidDataException>().With.Message.EqualTo(Messages.UnsupportedFile));
        }

        [Test]
        public void OutputPathMustBeAWritableFile()
        {
            var existing = Path.Combine(directory, "exists.wav");
            File.WriteAllText(existing, "");
            Assert.That(Program.CheckOutputPath(Path.Combine(directory, "new.wav"), false), Is.Null);
            Assert.That(Program.CheckOutputPath(existing, false), Is.EqualTo(Messages.OutputExists(existing)));
            Assert.That(Program.CheckOutputPath(existing, true), Is.Null);
            Assert.That(Program.CheckOutputPath(directory, true), Is.EqualTo(Messages.OutputIsDirectory(directory)));
            var trailing = Path.Combine(directory, "sub") + Path.DirectorySeparatorChar;
            Assert.That(Program.CheckOutputPath(trailing, true), Is.EqualTo(Messages.OutputIsDirectory(trailing)));
        }

        [Test]
        public void DefaultOutputPathAvoidsExistingFiles()
        {
            var input = Path.Combine(directory, "voice.mp3");
            var first = Path.Combine(directory, "voice_enhanced.wav");
            Assert.That(Program.GetDefaultOutputPath(input, false), Is.EqualTo(first));
            File.WriteAllText(first, "");
            Assert.That(Program.GetDefaultOutputPath(input, false), Is.EqualTo(Path.Combine(directory, "voice_enhanced (2).wav")));
            Assert.That(Program.GetDefaultOutputPath(input, true), Is.EqualTo(first));
        }
    }
}

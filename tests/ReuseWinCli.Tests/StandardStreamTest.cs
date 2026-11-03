using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ReuseWinCli.Audio;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// 標準入出力（パイプ）での読み書き。パイプは 1 回の読み込みで届いた分しか返さず、シークもできないので、それを再現するストリームで確かめる
    /// </summary>
    class StandardStreamTest
    {
        /// <summary>シークできず、1 回の Read で最大 maxRead バイトしか返さないストリーム</summary>
        sealed class PipeStream(byte[] data, int maxRead = int.MaxValue) : Stream
        {
            readonly MemoryStream inner = new(data);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, maxRead));
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>シークできない書き込み先</summary>
        sealed class PipeSink : Stream
        {
            public MemoryStream Inner { get; } = new();
            public bool Disposed { get; private set; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        static float[] ReadAll(IAudioReader reader)
        {
            var result = new System.Collections.Generic.List<float>();
            var buffer = new float[37];
            int count;
            while ((count = reader.Read(buffer)) > 0)
                result.AddRange(buffer.Take(count));
            return [.. result];
        }

        /// <summary>WAV のバイト列を組み立てる。fmt の前に LIST チャンク（奇数の長さ）を置き、チャンクの並びに依存しないことも確かめる</summary>
        static byte[] BuildWav(ushort tag, int bits, int channels, int sampleRate, byte[] data, uint? dataSize = null, bool extensible = false)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8);
            writer.Write(uint.MaxValue);
            writer.Write("WAVE"u8);
            writer.Write("LIST"u8);
            writer.Write(3);
            writer.Write("abc"u8);
            writer.Write((byte)0);
            writer.Write("fmt "u8);
            writer.Write(extensible ? 40 : 16);
            writer.Write(extensible ? (ushort)0xFFFE : tag);
            writer.Write((ushort)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bits / 8);
            writer.Write((ushort)(channels * bits / 8));
            writer.Write((ushort)bits);
            if (extensible)
            {
                writer.Write((ushort)22);
                writer.Write((ushort)bits);
                writer.Write(0u);
                writer.Write(tag);
                writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
            }
            writer.Write("data"u8);
            writer.Write(dataSize ?? (uint)data.Length);
            writer.Write(data);
            return stream.ToArray();
        }

        [Test]
        public void ReadsPcm16Wav()
        {
            short[] values = [0, 16384, -16384, short.MaxValue, short.MinValue, 1];
            var data = values.SelectMany(v => BitConverter.GetBytes(v)).ToArray();
            var reader = PcmStreamReader.OpenWav(new PipeStream(BuildWav(1, 16, 2, 44100, data), maxRead: 3));
            Assert.That(reader.SampleRate, Is.EqualTo(44100));
            Assert.That(reader.Channels, Is.EqualTo(2));
            Assert.That(ReadAll(reader), Is.EqualTo(values.Select(v => v / 32768f).ToArray()));
        }

        [Test]
        public void ReadsPcm24Wav()
        {
            int[] values = [0, 4194304, -4194304, 8388607, -8388608];
            var data = values.SelectMany(v => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16) }).ToArray();
            // 3 バイトのサンプルが 2 バイトずつ届いても、サンプルの途中で切れない
            var reader = PcmStreamReader.OpenWav(new PipeStream(BuildWav(1, 24, 1, 48000, data), maxRead: 2));
            Assert.That(ReadAll(reader), Is.EqualTo(values.Select(v => v / 8388608f).ToArray()));
        }

        [Test]
        public void ReadsPcm32AndFloatWav()
        {
            int[] ints = [0, 1 << 30, -(1 << 30)];
            var pcm32 = PcmStreamReader.OpenWav(new PipeStream(BuildWav(1, 32, 1, 16000, ints.SelectMany(BitConverter.GetBytes).ToArray())));
            Assert.That(ReadAll(pcm32), Is.EqualTo(new[] { 0f, 0.5f, -0.5f }));

            float[] floats = [0.25f, -1.5f, 2f];
            var ieee = PcmStreamReader.OpenWav(new PipeStream(BuildWav(3, 32, 1, 16000, floats.SelectMany(BitConverter.GetBytes).ToArray())));
            Assert.That(ReadAll(ieee), Is.EqualTo(floats));
        }

        [Test]
        public void ReadsExtensibleWav()
        {
            float[] floats = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f];
            var reader = PcmStreamReader.OpenWav(new PipeStream(BuildWav(3, 32, 3, 48000, floats.SelectMany(BitConverter.GetBytes).ToArray(), extensible: true)));
            Assert.That(reader.Channels, Is.EqualTo(3));
            Assert.That(ReadAll(reader), Is.EqualTo(floats));
        }

        /// <summary>data の大きさが分かっていれば、その後ろ（別のチャンク）は読まない。不明（0xFFFFFFFF）ならストリームの終わりまで読む</summary>
        [Test]
        public void HonorsDataSizeAndReadsToEndWhenUnknown()
        {
            short[] values = [100, 200, 300, 400];
            var data = values.SelectMany(v => BitConverter.GetBytes(v)).ToArray();
            var trailing = BuildWav(1, 16, 1, 8000, data, dataSize: 4).Concat("LIST"u8.ToArray()).ToArray();
            Assert.That(ReadAll(PcmStreamReader.OpenWav(new PipeStream(trailing))), Is.EqualTo(new[] { 100 / 32768f, 200 / 32768f }));

            var unknown = BuildWav(1, 16, 1, 8000, data, dataSize: uint.MaxValue);
            Assert.That(ReadAll(PcmStreamReader.OpenWav(new PipeStream(unknown))), Has.Length.EqualTo(4));
        }

        [Test]
        public void RejectsUnsupportedWav()
        {
            Assert.That(() => PcmStreamReader.OpenWav(new PipeStream("not a wav file"u8.ToArray())), Throws.InstanceOf<InvalidDataException>().With.Message.EqualTo(Messages.NotWav));
            Assert.That(() => PcmStreamReader.OpenWav(new PipeStream(BuildWav(1, 8, 1, 8000, [1, 2]))), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => PcmStreamReader.OpenWav(new PipeStream(BuildWav(1, 16, 1, 96000, [1, 2]))), Throws.InstanceOf<NotSupportedException>().With.Message.EqualTo(Messages.UnsupportedSampleRate(96000)));
        }

        [Test]
        public void ReadsRawPcm()
        {
            float[] floats = [0.5f, -0.25f, 1.25f, 0f];
            var f32 = new PcmStreamReader(new PipeStream(floats.SelectMany(BitConverter.GetBytes).ToArray(), maxRead: 5), PcmSampleFormat.Float32, 48000, 2);
            Assert.That(ReadAll(f32), Is.EqualTo(floats));

            short[] shorts = [8192, -8192];
            var s16 = new PcmStreamReader(new PipeStream(shorts.SelectMany(v => BitConverter.GetBytes(v)).ToArray(), maxRead: 1), PcmSampleFormat.Int16, 48000, 1);
            Assert.That(ReadAll(s16), Is.EqualTo(new[] { 0.25f, -0.25f }));
        }

        /// <summary>シークできない書き込み先には、大きさの欄を「不明」にし、fact を付けずに書く。読み直すと同じ値になる</summary>
        [Test]
        public void WavToPipeUsesUnknownSizesAndRoundTrips()
        {
            var samples = Enumerable.Range(0, 1000).Select(i => MathF.Sin(i * 0.1f) * 1.5f).ToArray();
            var sink = new PipeSink();
            using (var writer = new WavWriter(sink, 32000, 2))
            {
                writer.Write(samples.AsSpan(0, 400));
                writer.Write(samples.AsSpan(400));
            }
            Assert.That(sink.Disposed, Is.True);
            var bytes = sink.Inner.ToArray();
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)), Is.EqualTo(uint.MaxValue));
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            Assert.That(text, Does.Not.Contain("fact"));
            var dataIndex = text.IndexOf("data", StringComparison.Ordinal);
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(dataIndex + 4)), Is.EqualTo(uint.MaxValue));

            var reader = PcmStreamReader.OpenWav(new PipeStream(bytes));
            Assert.That(reader.SampleRate, Is.EqualTo(32000));
            Assert.That(reader.Channels, Is.EqualTo(2));
            Assert.That(ReadAll(reader), Is.EqualTo(samples));
        }

        [Test]
        public void RawWriterWritesLittleEndianFloats()
        {
            float[] samples = [0.5f, -2f, 1e-3f];
            var sink = new PipeSink();
            using (var writer = new RawWriter(sink))
                writer.Write(samples);
            Assert.That(sink.Inner.ToArray(), Is.EqualTo(samples.SelectMany(BitConverter.GetBytes).ToArray()));
        }
    }
}

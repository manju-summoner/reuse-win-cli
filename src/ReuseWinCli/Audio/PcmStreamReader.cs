using System;
using System.Buffers.Binary;
using System.IO;

namespace ReuseWinCli.Audio
{
    internal enum PcmSampleFormat
    {
        Int16,
        Int24,
        Int32,
        Float32,
    }

    /// <summary>
    /// シークできないストリーム（標準入力のパイプ）から、チャンネル交互の PCM を float に変換しながら読む。
    /// 長さは分からないので、ストリームの終わりまで読む
    /// </summary>
    internal sealed class PcmStreamReader : IAudioReader
    {
        readonly Stream stream;
        readonly PcmSampleFormat format;
        readonly int bytesPerSample;
        readonly long? dataBytes;
        long consumed;
        byte[] buffer = [];
        int carried;

        public int SampleRate { get; }
        public int Channels { get; }
        public long? EstimatedFrames => null;

        /// <param name="dataBytes">データの大きさ。分からなければ null（ストリームの終わりまで読む）</param>
        public PcmStreamReader(Stream stream, PcmSampleFormat format, int sampleRate, int channels, long? dataBytes = null)
        {
            if (sampleRate is < AudioLimits.MinSampleRate or > AudioLimits.MaxSampleRate)
                throw new NotSupportedException(Messages.UnsupportedSampleRate(sampleRate));
            if (channels is <= 0 or > AudioLimits.MaxChannels)
                throw new NotSupportedException(Messages.UnsupportedChannels(channels));
            this.stream = stream;
            this.format = format;
            this.dataBytes = dataBytes;
            SampleRate = sampleRate;
            Channels = channels;
            bytesPerSample = format switch
            {
                PcmSampleFormat.Int16 => 2,
                PcmSampleFormat.Int24 => 3,
                _ => 4,
            };
        }

        public int Read(Span<float> destination)
        {
            if (destination.IsEmpty)
                return 0;
            var wanted = destination.Length * bytesPerSample;
            if (buffer.Length < wanted)
                Array.Resize(ref buffer, wanted);

            // 前回の読み込みで余ったサンプルの途中のバイトを先頭に残している
            var filled = carried;
            while (filled < bytesPerSample)
            {
                var limit = Remaining(wanted - filled);
                if (limit <= 0)
                    break;
                var read = stream.Read(buffer, filled, limit);
                if (read <= 0)
                    break;
                filled += read;
                consumed += read;
            }
            // 1 サンプル分そろったら、いま届いている分だけで返す（パイプの読み込みは届いた分しか返さないので、溜めずに渡す）
            var samples = filled / bytesPerSample;
            Convert(buffer.AsSpan(0, samples * bytesPerSample), destination[..samples]);
            carried = filled - samples * bytesPerSample;
            if (carried > 0)
                buffer.AsSpan(samples * bytesPerSample, carried).CopyTo(buffer);
            return samples;
        }

        int Remaining(int wanted)
            => dataBytes is { } total ? (int)Math.Min(wanted, total - consumed) : wanted;

        void Convert(ReadOnlySpan<byte> source, Span<float> destination)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                var sample = source.Slice(i * bytesPerSample, bytesPerSample);
                destination[i] = format switch
                {
                    PcmSampleFormat.Int16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                    PcmSampleFormat.Int24 => ((sample[0] | (sample[1] << 8) | (sample[2] << 16)) << 8 >> 8) / 8388608f,
                    PcmSampleFormat.Int32 => (float)(BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0),
                    _ => BinaryPrimitives.ReadSingleLittleEndian(sample),
                };
            }
        }

        /// <summary>
        /// WAV のヘッダーを読み、データの先頭から読む PcmStreamReader を返す。
        /// パイプで流れてくる WAV は大きさの欄が「不明」（0xFFFFFFFF）や 0 のことがあるので、そのときはストリームの終わりまで読む
        /// https://learn.microsoft.com/en-us/windows/win32/api/mmreg/ns-mmreg-waveformatextensible
        /// </summary>
        public static PcmStreamReader OpenWav(Stream stream)
        {
            Span<byte> header = stackalloc byte[12];
            if (!TryReadExactly(stream, header) || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
                throw new InvalidDataException(Messages.NotWav);

            PcmSampleFormat? format = null;
            var sampleRate = 0;
            var channels = 0;
            Span<byte> chunkHeader = stackalloc byte[8];
            while (true)
            {
                if (!TryReadExactly(stream, chunkHeader))
                    throw new InvalidDataException(Messages.NotWav);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
                if (chunkHeader[..4].SequenceEqual("fmt "u8))
                {
                    if (size is < 16 or > 1024)
                        throw new InvalidDataException(Messages.NotWav);
                    var fmt = new byte[size];
                    if (!TryReadExactly(stream, fmt))
                        throw new InvalidDataException(Messages.NotWav);
                    (format, sampleRate, channels) = ParseFormat(fmt);
                    Skip(stream, size % 2);
                }
                else if (chunkHeader[..4].SequenceEqual("data"u8))
                {
                    if (format is null)
                        throw new InvalidDataException(Messages.NotWav);
                    long? dataBytes = size is 0 or uint.MaxValue ? null : size;
                    return new PcmStreamReader(stream, format.Value, sampleRate, channels, dataBytes);
                }
                else
                {
                    // LIST・fact などは読み飛ばす（チャンクは偶数バイトに揃えられている）
                    Skip(stream, size + size % 2);
                }
            }
        }

        static (PcmSampleFormat Format, int SampleRate, int Channels) ParseFormat(ReadOnlySpan<byte> fmt)
        {
            const ushort pcm = 1;
            const ushort ieeeFloat = 3;
            const ushort extensible = 0xFFFE;
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
            var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
            var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
            if (tag == extensible)
            {
                if (fmt.Length < 40)
                    throw new InvalidDataException(Messages.NotWav);
                // サブフォーマットの GUID の先頭 2 バイトが WAVE_FORMAT_PCM / WAVE_FORMAT_IEEE_FLOAT
                tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
            }
            PcmSampleFormat? format = (tag, bits) switch
            {
                (pcm, 16) => PcmSampleFormat.Int16,
                (pcm, 24) => PcmSampleFormat.Int24,
                (pcm, 32) => PcmSampleFormat.Int32,
                (ieeeFloat, 32) => PcmSampleFormat.Float32,
                _ => null,
            };
            if (format is null)
                throw new NotSupportedException(Messages.UnsupportedWavFormat(tag, bits));
            return (format.Value, sampleRate, channels);
        }

        static bool TryReadExactly(Stream stream, Span<byte> destination)
        {
            var filled = 0;
            while (filled < destination.Length)
            {
                var read = stream.Read(destination[filled..]);
                if (read <= 0)
                    return false;
                filled += read;
            }
            return true;
        }

        static void Skip(Stream stream, long count)
        {
            Span<byte> scratch = stackalloc byte[4096];
            while (count > 0)
            {
                var read = stream.Read(scratch[..(int)Math.Min(scratch.Length, count)]);
                if (read <= 0)
                    throw new InvalidDataException(Messages.NotWav);
                count -= read;
            }
        }
    }
}

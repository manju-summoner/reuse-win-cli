using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace ReuseWinCli.Audio
{
    /// <summary>
    /// 32bit float の WAV を書く。モデルの出力は 1 を超えることがあるので、整数に丸めて音を割らないよう float のまま残す。
    /// 3 チャンネル以上は WAVE_FORMAT_EXTENSIBLE で書く（WAVE_FORMAT_IEEE_FLOAT は 2 チャンネルまでしか想定されていない）
    /// https://learn.microsoft.com/en-us/windows/win32/api/mmreg/ns-mmreg-waveformatextensible
    /// </summary>
    internal sealed class WavWriter : IAudioWriter, IDisposable
    {
        /// <summary>音声データの大きさの上限。RIFF の大きさの欄は 32bit なので、ヘッダーの分を残す</summary>
        public const long MaxDataBytes = uint.MaxValue - 128;

        const ushort WaveFormatIeeeFloat = 3;
        const ushort WaveFormatExtensible = 0xFFFE;
        // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

        readonly Stream stream;
        readonly int channels;
        readonly long riffSizePosition;
        readonly long factPosition;
        readonly long dataSizePosition;
        long dataBytes;
        bool disposed;

        public WavWriter(Stream stream, int sampleRate, int channels)
        {
            this.stream = stream;
            this.channels = channels;
            var extensible = channels > 2;
            Span<byte> header = stackalloc byte[extensible ? 40 : 18];
            BinaryPrimitives.WriteUInt16LittleEndian(header, extensible ? WaveFormatExtensible : WaveFormatIeeeFloat);
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)channels);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], sampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], sampleRate * channels * sizeof(float));
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)(channels * sizeof(float)));
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
            BinaryPrimitives.WriteUInt16LittleEndian(header[16..], (ushort)(extensible ? 22 : 0));
            if (extensible)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(header[18..], 32);
                // チャンネルの配置は入力から引き継がないので、スピーカーの割り当てを決めない
                BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 0);
                SubtypeIeeeFloat.TryWriteBytes(header[24..]);
            }

            try
            {
                WriteAscii("RIFF");
                riffSizePosition = stream.Position;
                WriteUInt32(0);
                WriteAscii("WAVE");
                WriteAscii("fmt ");
                WriteUInt32((uint)header.Length);
                stream.Write(header);
                // PCM 以外の形式には fact チャンク（フレーム数）を付ける
                WriteAscii("fact");
                WriteUInt32(4);
                factPosition = stream.Position;
                WriteUInt32(0);
                WriteAscii("data");
                dataSizePosition = stream.Position;
                WriteUInt32(0);
            }
            catch
            {
                // 呼び出し元が書きかけのファイルを消せるよう、閉じてから伝える
                stream.Dispose();
                throw;
            }
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            var bytes = MemoryMarshal.AsBytes(samples);
            if (dataBytes + bytes.Length > MaxDataBytes)
                throw new IOException(Messages.WavTooLarge);
            stream.Write(bytes);
            dataBytes += bytes.Length;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                var end = stream.Position;
                stream.Position = riffSizePosition;
                WriteUInt32((uint)(end - 8));
                stream.Position = factPosition;
                WriteUInt32((uint)(dataBytes / (channels * sizeof(float))));
                stream.Position = dataSizePosition;
                WriteUInt32((uint)dataBytes);
                stream.Position = end;
            }
            finally
            {
                // ヘッダーの書き戻しに失敗しても閉じる。呼び出し元が書きかけのファイルを消せるようにする
                stream.Dispose();
            }
        }

        void WriteAscii(string text)
        {
            Span<byte> bytes = stackalloc byte[4];
            for (var i = 0; i < 4; i++)
                bytes[i] = (byte)text[i];
            stream.Write(bytes);
        }

        void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}

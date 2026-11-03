using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace ReuseWinCli.Audio
{
    /// <summary>
    /// Windows の Media Foundation で、音声ファイルや動画ファイルの最初の音声トラックを 32bit float に変換しながら読む。
    /// 読める形式は Windows に入っているデコーダーで決まる（wav・mp3・m4a/aac・mp4/mov の音声・wma・flac など）。
    /// 呼ぶ前に <see cref="MediaFactory.MFStartup"/> を済ませておく。
    /// https://learn.microsoft.com/en-us/windows/win32/medfound/source-reader
    /// </summary>
    internal sealed class MediaFoundationReader : IAudioReader, IDisposable
    {
        // RE-USE の公式が受け付けるサンプリングレートの範囲。外れるものは Media Foundation に変換させる
        const int MinSampleRate = AudioLimits.MinSampleRate;
        const int MaxSampleRate = AudioLimits.MaxSampleRate;

        readonly IMFSourceReader reader;
        float[] pending = [];
        int pendingOffset;
        bool ended;

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public long? EstimatedFrames { get; }

        /// <summary>入力のサンプリングレート。範囲外で変換したときだけ <see cref="SampleRate"/> と違う</summary>
        public int SourceSampleRate { get; }

        public MediaFoundationReader(string path)
        {
            try
            {
                reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
            }
            catch (SharpGen.Runtime.SharpGenException e)
            {
                throw new InvalidDataException(Messages.UnsupportedFile, e);
            }
            try
            {
                int nativeRate;
                try
                {
                    reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                    reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
                    using var native = reader.GetNativeMediaType((int)SourceReaderIndex.FirstAudioStream, 0);
                    nativeRate = (int)native.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                }
                catch (SharpGen.Runtime.SharpGenException e)
                {
                    // 音声のストリームが無いと MF_E_INVALIDSTREAMNUMBER になる
                    throw new InvalidDataException(Messages.NoAudioTrack, e);
                }
                var convert = nativeRate is < MinSampleRate or > MaxSampleRate;

                using (var request = MediaFactory.MFCreateMediaType())
                {
                    request.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    request.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float);
                    request.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 32u);
                    if (convert)
                        request.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)Math.Clamp(nativeRate, MinSampleRate, MaxSampleRate));
                    reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, request);
                }
                ReadCurrentFormat();

                // HE-AAC などは、コンテナーに書かれた形式（コア部分のサンプリングレート・チャンネル数）と、デコードした結果の形式が違い、
                // 最初のサンプルを読んだところで形式が変わったと知らされる。形式を確定させるため、最初のサンプルをここで読んでおく
                if (!Fill(allowFormatChange: true))
                    ended = true;
                SourceSampleRate = convert ? nativeRate : SampleRate;
                if (SampleRate is < MinSampleRate or > MaxSampleRate)
                    throw new NotSupportedException(Messages.UnsupportedSampleRate(SampleRate));
                if (Channels <= 0)
                    throw new NotSupportedException(Messages.UnknownChannels);

                // 長さ（100ns 単位）。分からない形式では取れないので、進捗の表示をあきらめる
                try
                {
                    var duration = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
                    if (duration.Value is ulong ticks)
                        EstimatedFrames = (long)(ticks * (ulong)SampleRate / 10_000_000);
                }
                catch (SharpGen.Runtime.SharpGenException)
                {
                }
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        public int Read(Span<float> destination)
        {
            var written = 0;
            while (written < destination.Length)
            {
                if (pendingOffset >= pending.Length)
                {
                    if (ended || !Fill())
                        break;
                    continue;
                }
                var count = Math.Min(destination.Length - written, pending.Length - pendingOffset);
                pending.AsSpan(pendingOffset, count).CopyTo(destination[written..]);
                pendingOffset += count;
                written += count;
            }
            return written;
        }

        void ReadCurrentFormat()
        {
            using var actual = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
            if (actual.GetGUID(MediaTypeAttributeKeys.Subtype) != AudioFormatGuids.Float || actual.GetUInt32(MediaTypeAttributeKeys.AudioBitsPerSample) != 32)
                throw new NotSupportedException(Messages.CannotConvertToFloat);
            SampleRate = (int)actual.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
            Channels = (int)actual.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
        }

        /// <summary>次のサンプルを読んで pending に入れる。終わりなら false</summary>
        /// <param name="allowFormatChange">形式が変わったら読み直す（最初のサンプルを読むまで）。false なら例外にする</param>
        bool Fill(bool allowFormatChange = false)
        {
            while (true)
            {
                using var sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out _, out var flags, out _);
                if ((flags & SourceReaderFlag.Error) != 0)
                    throw new InvalidDataException(Messages.ReadFailed);
                if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
                {
                    if (!allowFormatChange)
                        throw new NotSupportedException(Messages.FormatChanged);
                    ReadCurrentFormat();
                }
                if (sample is not null)
                {
                    using var buffer = sample.ConvertToContiguousBuffer();
                    buffer.Lock(out var pointer, out _, out var length);
                    try
                    {
                        var count = length / sizeof(float);
                        if (count > 0)
                        {
                            if (pending.Length != count)
                                pending = new float[count];
                            Marshal.Copy(pointer, pending, 0, count);
                            pendingOffset = 0;
                            if ((flags & SourceReaderFlag.EndOfStream) != 0)
                                ended = true;
                            return true;
                        }
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }
                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    ended = true;
                    return false;
                }
            }
        }

        public void Dispose() => reader.Dispose();
    }
}

using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ReuseWinCli.Audio
{
    /// <summary>
    /// 標準入出力のパイプの読み書きを、中止されたら待たずに OperationCanceledException にする。
    /// パイプの同期の読み書きは、相手がパイプを開いたまま送らない・読まないと返らない。取り消しの API（CancelIoEx）は、
    /// 中止した時点で始まっていない読み書きには効かないので、読み書きは別のスレッドで行い、こちらは中止されたら待つのをやめる。
    /// 待つのをやめた読み書きのスレッドは止まったまま残るが、中止の後はプロセスが終わる
    /// </summary>
    internal sealed class CancellableStream(Stream inner, CancellationToken token) : Stream
    {
        const int MaxReadBytes = 64 * 1024;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        // 読み書きのスレッドには、このためだけに作った配列を渡す。待つのをやめた後もそのスレッドは配列を読み書きし続けるので、
        // 呼び出し元の配列や、基底の Span 版が ArrayPool から借りて返す配列を渡すと、返した後の配列を書き換えてしまう
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            // Read は要求より少なく返してよい（呼び出し側は足りない分を読み直す）ので、呼ぶたびに作る配列を 64KB に抑える
            var own = new byte[Math.Min(buffer.Length, MaxReadBytes)];
            var read = Run(() => inner.Read(own, 0, own.Length));
            own.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var own = buffer.ToArray();
            Run(() =>
            {
                inner.Write(own, 0, own.Length);
                return 0;
            });
        }

        public override void Flush() => Run(() =>
        {
            inner.Flush();
            return 0;
        });

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        T Run<T>(Func<T> operation)
        {
            token.ThrowIfCancellationRequested();
            var task = Task.Run(operation);
            try
            {
                task.Wait(token);
            }
            catch (AggregateException e) when (e.InnerExceptions.Count == 1)
            {
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
            return task.Result;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

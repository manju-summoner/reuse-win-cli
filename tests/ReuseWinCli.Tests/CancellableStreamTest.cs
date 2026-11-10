using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ReuseWinCli.Audio;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// パイプの相手が開いたまま書き込まない・読み取らないときでも、中止で読み書きの待機から抜けられる。
    /// 中止した時点で読み書きが開始していない場合（推論を待っている間に Ctrl+C が押された場合）も含む
    /// </summary>
    class CancellableStreamTest
    {
        static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(300);
        static readonly TimeSpan Released = TimeSpan.FromSeconds(5);

        [Test]
        public void PendingReadIsReleasedByCancellation()
        {
            using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            using var reader = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
            using var cancellation = new CancellationTokenSource();
            var stream = new CancellableStream(reader, cancellation.Token);

            var read = Task.Run(() => stream.Read(new byte[16], 0, 16));
            Assert.That(read.Wait(Blocked), Is.False, "書き込み側が開いたままなので、中止するまで返らないはず");

            cancellation.Cancel();
            Assert.That(() => read.Wait(Released), Throws.InstanceOf<AggregateException>().With.InnerException.InstanceOf<OperationCanceledException>());
        }

        /// <summary>
        /// 待機をやめた後に受信したデータで、呼び出し元の配列が書き換わらない（読み込みのスレッドはブロックしたまま残り、後で完了するため）
        /// </summary>
        [Test]
        public void AbandonedReadDoesNotTouchCallerBuffer()
        {
            using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            using var reader = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
            using var cancellation = new CancellationTokenSource();
            var stream = new CancellableStream(reader, cancellation.Token);
            var buffer = new byte[16];

            var read = Task.Run(() => stream.Read(buffer, 0, buffer.Length));
            Assert.That(read.Wait(Blocked), Is.False);
            cancellation.Cancel();
            Assert.That(() => read.Wait(Released), Throws.InstanceOf<AggregateException>());

            // ブロックしたままの読み込みが、ここで書き込んだデータを受信して完了する
            writer.Write([0xAA, 0xBB, 0xCC, 0xDD], 0, 4);
            writer.Flush();
            Thread.Sleep(300);
            Assert.That(buffer, Is.All.EqualTo(0));
        }

        [Test]
        public void ReadAfterCancellationDoesNotBlock()
        {
            using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            using var reader = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
            using var cancellation = new CancellationTokenSource();
            var stream = new CancellableStream(reader, cancellation.Token);

            cancellation.Cancel();
            var read = Task.Run(() => stream.Read(new byte[16], 0, 16));
            Assert.That(() => read.Wait(Released), Throws.InstanceOf<AggregateException>().With.InnerException.InstanceOf<OperationCanceledException>());
        }

        [Test]
        public void PendingWriteIsReleasedByCancellation()
        {
            using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            using var reader = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
            using var cancellation = new CancellationTokenSource();
            var stream = new CancellableStream(writer, cancellation.Token);

            // 読み取り側が読み取らないので、パイプのバッファが埋まった時点で書き込みがブロックする
            var write = Task.Run(() => stream.Write(new byte[16 * 1024 * 1024], 0, 16 * 1024 * 1024));
            Assert.That(write.Wait(Blocked), Is.False, "読み取り側が読み取らないので、中止するまで返らないはず");

            cancellation.Cancel();
            Assert.That(() => write.Wait(Released), Throws.InstanceOf<AggregateException>().With.InnerException.InstanceOf<OperationCanceledException>());
        }

        [Test]
        public void PassesDataAndErrorsThrough()
        {
            using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            using var reader = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
            var output = new CancellableStream(writer, CancellationToken.None);
            var input = new CancellableStream(reader, CancellationToken.None);

            output.Write([1, 2, 3], 0, 3);
            var buffer = new byte[8];
            Assert.That(input.Read(buffer, 0, buffer.Length), Is.EqualTo(3));
            Assert.That(buffer[..3], Is.EqualTo(new byte[] { 1, 2, 3 }));

            // 読み取り側が閉じた後の書き込みエラーは、ラップされずにそのままスローされる（呼び出し元がパイプの切断として判別する）
            reader.Dispose();
            Assert.That(() => output.Write([4], 0, 1), Throws.InstanceOf<IOException>());
        }
    }
}

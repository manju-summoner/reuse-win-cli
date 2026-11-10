using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReuseWinCli
{
    /// <summary>
    /// 標準入出力のハンドルを、そのまま FileStream で開く。
    /// Console.OpenStandardOutput は、読み取り側が終了してパイプが閉じられたときの書き込みエラーを無視するので、閉じられた後も最後まで推論を続けてしまう。
    /// FileStream なら IOException がスローされる。入力も同じ方法で開く（書き込み側が閉じたら、どちらも終端（0 バイト）を返す）
    /// </summary>
    internal static partial class StandardStreams
    {
        const int StdInputHandle = -10;
        const int StdOutputHandle = -11;

        public static FileStream OpenInput() => Open(GetStdHandle(StdInputHandle), FileAccess.Read);

        public static FileStream OpenOutput() => Open(GetStdHandle(StdOutputHandle), FileAccess.Write);

        static FileStream Open(nint handle, FileAccess access)
            => new(new SafeFileHandle(handle, ownsHandle: false), access, bufferSize: 0);

        [LibraryImport("kernel32.dll")]
        private static partial nint GetStdHandle(int standardHandle);
    }
}

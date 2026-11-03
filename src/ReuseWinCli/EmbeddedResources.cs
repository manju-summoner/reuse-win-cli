using System;
using System.IO;
using System.Reflection;

namespace ReuseWinCli
{
    /// <summary>
    /// exe に埋め込んだリソース（シェーダー・重み・ライセンス）を読む。名前は csproj と *.targets の LogicalName
    /// </summary>
    internal static class EmbeddedResources
    {
        public static Stream Open(string name)
            => typeof(EmbeddedResources).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"埋め込みリソース {name} がありません。");

        public static byte[] ReadAllBytes(string name)
        {
            using var stream = Open(name);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        /// <summary>
        /// リソースの中身をコピーせずに読む。埋め込みリソースは exe のイメージにそのまま載っているので、ポインターで触れる
        /// </summary>
        public static unsafe T Read<T>(string name, ReadOnlySpanFunc<T> reader)
        {
            using var stream = Open(name);
            if (stream is UnmanagedMemoryStream unmanaged)
                return reader(new ReadOnlySpan<byte>(unmanaged.PositionPointer, checked((int)unmanaged.Length)));
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return reader(bytes);
        }

        public delegate T ReadOnlySpanFunc<T>(ReadOnlySpan<byte> bytes);

        public static string ReadText(string name)
        {
            using var reader = new StreamReader(Open(name));
            return reader.ReadToEnd();
        }
    }
}

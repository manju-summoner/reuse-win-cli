using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using ReuseWinCli.Enhancement;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// 公式の重みと PyTorch の出力で比べるテストの入力。
    /// 重みは exe に埋め込んだもの（ビルド時に Hugging Face から取ってきたもの）を使う。
    /// PyTorch の出力（生の float 列）は配布できないので、環境変数 REUSE_FIXTURE_DIR でフォルダを指定したときだけ使う。中身は README の「テスト」
    /// </summary>
    internal static class Fixture
    {
        /// <summary>fixture のフォルダ。指定されていない・ファイルが無いときはテストを飛ばす</summary>
        public static string RequireDirectory(string requiredFile)
        {
            var root = Environment.GetEnvironmentVariable("REUSE_FIXTURE_DIR");
            Assume.That(root, Is.Not.Null.And.Not.Empty, "REUSE_FIXTURE_DIR が設定されていません");
            Assume.That(File.Exists(Path.Combine(root!, requiredFile)), Is.True, $"fixture に {requiredFile} がありません");
            return root!;
        }

        public static Dictionary<string, SafeTensor> ReadOfficialWeights()
            => EmbeddedResources.Read("model.safetensors", bytes => SafeTensors.Read(bytes));

        public static float[] ReadFloats(string path) => MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(path)).ToArray();

        public static double Snr(float[] actual, float[] expected)
        {
            var signal = expected.Sum(v => (double)v * v);
            var error = actual.Zip(expected, (a, b) => (double)(a - b) * (a - b)).Sum();
            return 10 * Math.Log10(signal / error);
        }
    }
}

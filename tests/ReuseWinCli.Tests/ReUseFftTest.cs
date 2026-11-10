using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using ReuseWinCli.Enhancement;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// STFT の FFT を、倍精度の素朴な DFT と比較する。
    /// 長さはサンプリングレートで決まる（48kHz は 1920、44.1kHz は 1764、11025Hz は 442）ので、2 のべき乗でない長さと、
    /// 小さな素因数に分解しない長さ（素因数が大きいとき）の両方を確認する
    /// </summary>
    class ReUseFftTest
    {
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(7)]
        [TestCase(16)]
        [TestCase(122)]
        [TestCase(134)]
        [TestCase(202)]
        [TestCase(320)]
        [TestCase(442)]
        [TestCase(1764)]
        [TestCase(1920)]
        public void MatchesNaiveDft(int length)
        {
            var random = new Random(length);
            var input = new Complex[length];
            for (var i = 0; i < length; i++)
                input[i] = new Complex(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);

            var re = input.Select(v => v.Real).ToArray();
            var im = input.Select(v => v.Imaginary).ToArray();
            new ReUseFft(length).Forward(re, im, new double[length], new double[length]);

            var maxError = 0d;
            var maxMagnitude = 0d;
            for (var k = 0; k < length; k++)
            {
                var expected = Complex.Zero;
                for (var n = 0; n < length; n++)
                {
                    var angle = -2 * Math.PI * ((long)k * n % length) / length;
                    expected += input[n] * new Complex(Math.Cos(angle), Math.Sin(angle));
                }
                maxError = Math.Max(maxError, (new Complex(re[k], im[k]) - expected).Magnitude);
                maxMagnitude = Math.Max(maxMagnitude, expected.Magnitude);
            }
            TestContext.Out.WriteLine($"n={length} max error {maxError:E3} (max magnitude {maxMagnitude:F3})");
            Assert.That(maxError / maxMagnitude, Is.LessThan(1e-13));
        }
    }
}

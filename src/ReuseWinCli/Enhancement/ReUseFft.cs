using System;
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace ReuseWinCli.Enhancement
{
    /// <summary>
    /// STFT に使う倍精度の FFT（X[k] = Σ x[n] e^{-2πikn/N}、スケーリングなし）。
    /// 長さはサンプリングレートで決まり 2 のべき乗にならない（48kHz は 1920 = 2⁷·3·5）。MathNet の Fourier.Forward は
    /// 2 のべき乗でない長さを Bluestein 法で計算するので遅い。長さを小さな素因数に分け、Stockham の自動整列の形で各段を計算する。
    /// 素因数が大きいと 1 段の計算量（長さ × 素因数）が Bluestein 法より重くなるので、そのときは MathNet で計算する
    /// </summary>
    internal sealed class ReUseFft
    {
        const int MaxRadix = 64;

        readonly int[]? factors;
        // 各段の p 点の DFT の係数 [r·p + t] = e^{-2πirt/p}
        readonly double[][] stageCos = [];
        readonly double[][] stageSin = [];
        // e^{-2πik/N} = cos[k] + i·sin[k]
        readonly double[] cos;
        readonly double[] sin;

        public int Length { get; }

        public ReUseFft(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            Length = length;
            cos = new double[length];
            sin = new double[length];
            for (var k = 0; k < length; k++)
            {
                var angle = 2 * Math.PI * k / length;
                cos[k] = Math.Cos(angle);
                // 0.0 - で、k = 0 の -0.0 を +0.0 にそろえる
                sin[k] = 0.0 - Math.Sin(angle);
            }

            factors = Factorize(length);
            if (factors is null)
                return;
            stageCos = new double[factors.Length][];
            stageSin = new double[factors.Length][];
            for (var i = 0; i < factors.Length; i++)
            {
                var p = factors[i];
                stageCos[i] = new double[p * p];
                stageSin[i] = new double[p * p];
                for (var r = 0; r < p; r++)
                {
                    for (var t = 0; t < p; t++)
                    {
                        var k = r * t % p * (length / p);
                        stageCos[i][r * p + t] = cos[k];
                        stageSin[i][r * p + t] = sin[k];
                    }
                }
            }
        }

        static int[]? Factorize(int length)
        {
            var result = new System.Collections.Generic.List<int>();
            var rest = length;
            while (rest % 4 == 0)
            {
                result.Add(4);
                rest /= 4;
            }
            while (rest % 2 == 0)
            {
                result.Add(2);
                rest /= 2;
            }
            for (var p = 3; p * p <= rest; p += 2)
            {
                while (rest % p == 0)
                {
                    result.Add(p);
                    rest /= p;
                }
            }
            if (rest > 1)
                result.Add(rest);
            foreach (var factor in result)
            {
                if (factor > MaxRadix)
                    return null;
            }
            return [.. result];
        }

        /// <summary>re と im（どちらも Length 個）をその場で変換する</summary>
        /// <param name="workRe">作業用。Length 個以上</param>
        /// <param name="workIm">作業用。Length 個以上</param>
        public void Forward(double[] re, double[] im, double[] workRe, double[] workIm)
        {
            if (re.Length != Length || im.Length != Length)
                throw new ArgumentException("長さが違います。", nameof(re));
            if (workRe.Length < Length || workIm.Length < Length)
                throw new ArgumentException("作業用の配列が足りません。", nameof(workRe));
            if (factors is null)
            {
                ForwardBluestein(re, im);
                return;
            }

            Span<double> valueRe = stackalloc double[MaxRadix];
            Span<double> valueIm = stackalloc double[MaxRadix];
            Span<double> rotationRe = stackalloc double[MaxRadix];
            Span<double> rotationIm = stackalloc double[MaxRadix];
            var (sourceRe, sourceIm, targetRe, targetIm) = (re, im, workRe, workIm);
            // n: この段で残っている系列の長さ、stride: これまでの段の素因数の積
            var n = Length;
            var stride = 1;
            for (var stage = 0; stage < factors.Length; stage++)
            {
                var p = factors[stage];
                var dftCos = stageCos[stage];
                var dftSin = stageSin[stage];
                var m = n / p;
                for (var j = 0; j < m; j++)
                {
                    // e^{-2πijt/n} = e^{-2πijt·stride/N}
                    for (var t = 0; t < p; t++)
                    {
                        var k = (int)((long)j * t * stride % Length);
                        rotationRe[t] = cos[k];
                        rotationIm[t] = sin[k];
                    }
                    for (var q = 0; q < stride; q++)
                    {
                        for (var r = 0; r < p; r++)
                        {
                            var index = q + stride * (j + m * r);
                            valueRe[r] = sourceRe[index];
                            valueIm[r] = sourceIm[index];
                        }
                        var output = q + stride * p * j;
                        if (p == 2)
                        {
                            Store(targetRe, targetIm, output, valueRe[0] + valueRe[1], valueIm[0] + valueIm[1], rotationRe[0], rotationIm[0]);
                            Store(targetRe, targetIm, output + stride, valueRe[0] - valueRe[1], valueIm[0] - valueIm[1], rotationRe[1], rotationIm[1]);
                        }
                        else if (p == 4)
                        {
                            // e^{-2πirt/4} は 1, -i, -1, i なので掛け算が要らない
                            var sum02Re = valueRe[0] + valueRe[2];
                            var sum02Im = valueIm[0] + valueIm[2];
                            var diff02Re = valueRe[0] - valueRe[2];
                            var diff02Im = valueIm[0] - valueIm[2];
                            var sum13Re = valueRe[1] + valueRe[3];
                            var sum13Im = valueIm[1] + valueIm[3];
                            var diff13Re = valueRe[1] - valueRe[3];
                            var diff13Im = valueIm[1] - valueIm[3];
                            Store(targetRe, targetIm, output, sum02Re + sum13Re, sum02Im + sum13Im, rotationRe[0], rotationIm[0]);
                            Store(targetRe, targetIm, output + stride, diff02Re + diff13Im, diff02Im - diff13Re, rotationRe[1], rotationIm[1]);
                            Store(targetRe, targetIm, output + stride * 2, sum02Re - sum13Re, sum02Im - sum13Im, rotationRe[2], rotationIm[2]);
                            Store(targetRe, targetIm, output + stride * 3, diff02Re - diff13Im, diff02Im + diff13Re, rotationRe[3], rotationIm[3]);
                        }
                        else
                        {
                            for (var t = 0; t < p; t++)
                            {
                                var sumRe = valueRe[0];
                                var sumIm = valueIm[0];
                                for (var r = 1; r < p; r++)
                                {
                                    var c = dftCos[r * p + t];
                                    var s = dftSin[r * p + t];
                                    sumRe += valueRe[r] * c - valueIm[r] * s;
                                    sumIm += valueRe[r] * s + valueIm[r] * c;
                                }
                                Store(targetRe, targetIm, output + stride * t, sumRe, sumIm, rotationRe[t], rotationIm[t]);
                            }
                        }
                    }
                }

                (sourceRe, sourceIm, targetRe, targetIm) = (targetRe, targetIm, sourceRe, sourceIm);
                n = m;
                stride *= p;
            }
            if (!ReferenceEquals(sourceRe, re))
            {
                Array.Copy(sourceRe, re, Length);
                Array.Copy(sourceIm, im, Length);
            }
        }

        static void Store(double[] targetRe, double[] targetIm, int index, double re, double im, double rotationRe, double rotationIm)
        {
            targetRe[index] = re * rotationRe - im * rotationIm;
            targetIm[index] = re * rotationIm + im * rotationRe;
        }

        void ForwardBluestein(double[] re, double[] im)
        {
            var buffer = new Complex[Length];
            for (var i = 0; i < Length; i++)
                buffer[i] = new Complex(re[i], im[i]);
            Fourier.Forward(buffer, FourierOptions.Matlab);
            for (var i = 0; i < Length; i++)
            {
                re[i] = buffer[i].Real;
                im[i] = buffer[i].Imaginary;
            }
        }
    }
}

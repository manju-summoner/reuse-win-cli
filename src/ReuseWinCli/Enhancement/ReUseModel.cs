using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReuseWinCli.Enhancement
{
    /// <summary>
    /// RE-USE（nvidia/NVIDIA-NemotronLabs-RE-USE）の推論。モノラルの音声を受け取り、補正した同じ長さの音声を返す。
    /// 公式の inference.py と同じ手順（STFT → エンコーダー → TF-Mamba 30 段 → 振幅と位相のデコーダー → 無音フレームの処理 → iSTFT）を GPU で行う。
    /// STFT の長さはサンプリングレートに比例させる（8kHz で n_fft 320・hop 40）。2 の累乗にならない。
    /// 順変換は CPU の倍精度の FFT で求める。モデルは入力の位相に敏感で、ほぼ無音のビンの位相の誤差（fp32 の行列積の DFT で最大 1e-3 rad）だけで、
    /// 8kHz の出力が公式と -43dB ずれる。逆変換はモデルの入力にならないので、GPU の行列積で求める。
    /// </summary>
    internal sealed class ReUseModel : IDisposable
    {
        const int C = 64;
        const int DenseDepth = 4;
        const float NormEpsilon = 1e-5f;
        const int BaseSampleRate = 8000;
        const int BaseFft = 320;
        const int BaseHop = 40;

        readonly ReUseComputeDevice device;
        readonly List<IDisposable> owned = [];
        readonly Dictionary<string, ReUseBuffer> work = [];
        ReUseMambaStack mamba = null!;
        ConvLayer encoderIn = null!;
        ConvLayer[] encoderDense = null!;
        ConvLayer encoderOut = null!;
        Decoder magnitudeDecoder = null!;
        Decoder phaseDecoder = null!;
        StftPlan? plan;

        public ReUseModel(ReUseComputeDevice device, IReadOnlyDictionary<string, SafeTensor> tensors)
        {
            this.device = device;
            try
            {
                Load(tensors);
            }
            catch
            {
                // 途中までに作った重みのバッファを共有のデバイスに残さない
                Dispose();
                throw;
            }
        }

        void Load(IReadOnlyDictionary<string, SafeTensor> tensors)
        {
            var blocks = 0;
            while (tensors.ContainsKey($"TSMamba.{blocks}.time_mamba.norm.weight"))
                blocks++;
            if (blocks == 0)
                throw new KeyNotFoundException("TF-Mamba ブロックの重みがありません。");
            mamba = new ReUseMambaStack(device, tensors, blocks);
            owned.Add(mamba);

            encoderIn = LoadConv(tensors, "dense_encoder.dense_conv_1.", 2, false);
            encoderDense = LoadDense(tensors, "dense_encoder.dense_block.dense_block.");
            encoderOut = LoadConv(tensors, "dense_encoder.dense_conv_2.", C, false);
            magnitudeDecoder = LoadDecoder(tensors, "mask_decoder.", ["final_conv."]);
            phaseDecoder = LoadDecoder(tensors, "phase_decoder.", ["phase_conv_r.", "phase_conv_i."]);
        }

        public static (int Fft, int Hop) GetStftSize(int sampleRate)
            => (MakeEven(BaseFft * sampleRate / BaseSampleRate), MakeEven(BaseHop * sampleRate / BaseSampleRate));

        static int MakeEven(int value) => value % 2 == 0 ? value : value + 1;

        /// <summary>
        /// samples サンプルを1回で処理したときの、いちばん大きい作業用バッファのバイト数（エンコーダーの入口・デコーダーの出口の 64 チャンネルの活性と、
        /// TF-Mamba の作業用バッファの大きい方）。TF-Mamba の行数が上限を超えるなら long.MaxValue
        /// </summary>
        public static long GetLargestBufferBytes(int samples, int sampleRate)
        {
            var (fft, hop) = GetStftSize(sampleRate);
            var frames = 1 + Math.Max(samples, fft / 2 + 1) / hop;
            var bins = fft / 2 + 1;
            var h = frames + 2;
            var w = bins + 2;
            var h2 = (h - 1) / 4 + 1;
            var w2 = (w - 3) / 2 + 1;
            if ((long)h2 * w2 > ReUseMambaStack.MaxRows)
                return long.MaxValue;
            var positions = Math.Max((long)h * w, 4L * h2 * 2 * w2);
            return Math.Max(positions * C * sizeof(float), ReUseMambaStack.GetLargestBufferBytes(h2 * w2));
        }

        public float[] Enhance(ReadOnlySpan<float> input, int sampleRate)
        {
            var prepared = Prepare(input, sampleRate);
            return prepared is null ? [] : Finish(prepared, Infer(prepared));
        }

        /// <summary>
        /// 推論の CPU の前処理（STFT とモデルの入力づくり）。GPU もモデルも使わないので、ほかの推論の GPU の処理と並べて走らせられる。入力が空なら null
        /// </summary>
        public static PreparedInput? Prepare(ReadOnlySpan<float> input, int sampleRate)
        {
            var originalLength = input.Length;
            if (originalLength == 0)
                return null;
            var (fft, hop) = GetStftSize(sampleRate);
            var bins = fft / 2 + 1;
            var window = CreateWindow(fft);

            // center=True の reflect の詰め物は入力より短くないといけない。短すぎる入力は無音を足して処理し、後で切り詰める
            var samples = input.ToArray();
            if (samples.Length <= fft / 2)
                Array.Resize(ref samples, fft / 2 + 1);

            var frames = 1 + samples.Length / hop;
            var spectrum = Stft(fft, hop, window, samples, frames);
            var modelInput = BuildModelInput(spectrum, frames, bins, frames + 2, bins + 2);
            return new PreparedInput(sampleRate, fft, hop, window, originalLength, frames, modelInput);
        }

        /// <summary>
        /// 推論の CPU の後処理（iSTFT の重ね合わせ）
        /// </summary>
        /// <param name="framed"><see cref="Infer"/> の結果</param>
        public static float[] Finish(PreparedInput prepared, float[] framed)
        {
            var output = OverlapAdd(prepared.Fft, prepared.Hop, prepared.Window, framed, prepared.Frames);
            var result = new float[prepared.OriginalLength];
            var available = Math.Min(output.Length, prepared.OriginalLength);
            output.AsSpan(0, available).CopyTo(result);
            // 公式の pad_or_trim_to_match と同じく、iSTFT が届かない末尾は 1e-8 で埋める
            result.AsSpan(available).Fill(1e-8f);
            return result;
        }

        /// <summary>
        /// 推論の GPU の処理（エンコーダー → TF-Mamba → デコーダー → 逆 DFT）。作業用バッファを使い回すので、呼び出し側が 1 つずつ呼ぶ
        /// </summary>
        /// <returns>窓を掛けた逆変換のフレーム [時間][n_fft]</returns>
        public float[] Infer(PreparedInput prepared)
        {
            var stft = GetPlan(prepared.SampleRate);
            var frames = prepared.Frames;
            var height = frames + 2;
            var width = stft.Bins + 2;
            var input2 = prepared.ModelInput;

            // エンコーダー
            var inputBuffer = Work("input", input2.Length);
            inputBuffer.Upload(input2);
            var x0 = Work("encoder0", height * width * C);
            Conv(inputBuffer, height, width, 2, encoderIn, 0, x0, height, width, width, 1, 1, 0, 0, ReUseKernels.Shuffle.None, 1);
            Norm(x0, height * width, encoderIn);
            var dense = DenseBlock(encoderDense, x0, height, width, "encoder");

            var h2 = (height - 1) / 4 + 1;
            var w2 = (width - 3) / 2 + 1;
            var activation = Work("activation", h2 * w2 * C);
            Conv(dense, height, width, C, encoderOut, 0, activation, h2, w2, w2, 4, 2, 0, 0, ReUseKernels.Shuffle.None, 1);
            Norm(activation, h2 * w2, encoderOut);

            mamba.Run(activation, h2, w2);

            // デコーダーの出力は [4・h2][2・w2]。先頭の frames × bins だけを使う
            var outHeight = h2 * 4;
            var outWidth = w2 * 2;
            var amplitude = new float[outHeight * outWidth];
            Decode(magnitudeDecoder, activation, h2, w2).Readback(amplitude);
            var phaseParts = new float[outHeight * outWidth * 2];
            Decode(phaseDecoder, activation, h2, w2).Readback(phaseParts);

            return InverseFrames(stft, amplitude, phaseParts, outWidth, frames);
        }

        /// <returns>[時間][実部 bins 個・虚部 bins 個]</returns>
        internal static double[] Stft(int fft, int hop, double[] window, float[] samples, int frames)
        {
            var pad = fft / 2;
            var length = samples.Length;
            var bins = fft / 2 + 1;
            var spectrum = new double[frames * bins * 2];
            var plan = new ReUseFft(fft);
            Parallel.For(0, frames, () => (Re: new double[fft], Im: new double[fft], WorkRe: new double[fft], WorkIm: new double[fft]), (t, _, local) =>
            {
                var (re, im) = (local.Re, local.Im);
                for (var i = 0; i < fft; i++)
                {
                    var index = t * hop + i - pad;
                    if (index < 0)
                        index = -index;
                    else if (index >= length)
                        index = 2 * (length - 1) - index;
                    re[i] = samples[index] * window[i];
                    im[i] = 0;
                }
                plan.Forward(re, im, local.WorkRe, local.WorkIm);
                for (var k = 0; k < bins; k++)
                {
                    spectrum[t * bins * 2 + k] = re[k];
                    // 実数の FFT（torch.stft）では DC と Nyquist の虚部がちょうど 0 になる。丸めの残り（±1e-15 程度）を残すと、
                    // 実部が負のときに位相が +π と -π で入れ替わり、モデルの入力が 2π ずれる
                    spectrum[t * bins * 2 + bins + k] = k == 0 || k == fft / 2 ? 0 : im[k];
                }
                return local;
            }, _ => { });
            return spectrum;
        }

        /// <summary>
        /// [時間+2][周波数+2][振幅・位相] の入力を作る。振幅は log1p で圧縮する。末尾の 2 行 2 列は公式と同じく 0 を詰める
        /// </summary>
        internal static float[] BuildModelInput(double[] spectrum, int frames, int bins, int height, int width)
        {
            var input = new float[height * width * 2];
            Parallel.For(0, frames, t =>
            {
                for (var f = 0; f < bins; f++)
                {
                    // -0.0 を +0.0 にそろえる。無音のフレームの FFT には -0.0 が混じり、Atan2(+0, -0) が π になる（torch では 0）
                    var re = spectrum[t * bins * 2 + f] + 0.0;
                    var im = spectrum[t * bins * 2 + bins + f] + 0.0;
                    var index = (t * width + f) * 2;
                    input[index] = (float)Math.Log(1 + Math.Sqrt(re * re + im * im));
                    input[index + 1] = (float)Math.Atan2(im, re);
                }
            });
            return input;
        }

        float[] InverseFrames(StftPlan stft, float[] amplitude, float[] phaseParts, int storeWidth, int frames)
        {
            var bins = stft.Bins;
            // 公式の「奇妙なスイープ」対策: 振幅が 0 になったビンが半分を超えるフレームは全体を 0 にする
            // この間 GPU は空くので、フレームごとに並列に求める
            var spectrum = new float[frames * bins * 2];
            Parallel.For(0, frames, t =>
            {
                var zeros = 0;
                for (var f = 0; f < bins; f++)
                {
                    if (amplitude[t * storeWidth + f] <= 0)
                        zeros++;
                }
                if (zeros * 2 > bins)
                    return;
                for (var f = 0; f < bins; f++)
                {
                    var a = Math.Max(amplitude[t * storeWidth + f], 0);
                    var magnitude = Math.Exp(a) - 1;
                    var index = (t * storeWidth + f) * 2;
                    var phase = Math.Atan2(phaseParts[index + 1], phaseParts[index]);
                    spectrum[t * bins * 2 + f] = (float)(magnitude * Math.Cos(phase));
                    spectrum[t * bins * 2 + bins + f] = (float)(magnitude * Math.Sin(phase));
                }
            });

            var spectrumBuffer = Work("istftSpectrum", spectrum.Length);
            spectrumBuffer.Upload(spectrum);
            var framesBuffer = Work("istftFrames", frames * stft.Fft);
            ReUseKernels.Linear(device, spectrumBuffer, stft.Inverse, framesBuffer, frames, stft.Fft, bins * 2);
            var framed = new float[frames * stft.Fft];
            framesBuffer.Readback(framed);
            return framed;
        }

        /// <summary>
        /// 逆変換のフレームを重ね合わせて、窓の 2 乗の和で割る（torch.istft と同じ）。center=True の詰め物の分を先頭から捨てる
        /// </summary>
        static float[] OverlapAdd(int fft, int hop, double[] window, float[] framed, int frames)
        {
            var total = fft + hop * (frames - 1);
            var sum = new double[total];
            var envelope = new double[total];
            for (var t = 0; t < frames; t++)
            {
                var start = t * hop;
                for (var i = 0; i < fft; i++)
                {
                    sum[start + i] += framed[t * fft + i];
                    envelope[start + i] += window[i] * window[i];
                }
            }
            var pad = fft / 2;
            var output = new float[hop * (frames - 1)];
            for (var i = 0; i < output.Length; i++)
                output[i] = envelope[pad + i] > 1e-11 ? (float)(sum[pad + i] / envelope[pad + i]) : 0;
            return output;
        }

        ReUseBuffer Decode(Decoder decoder, ReUseBuffer activation, int h2, int w2)
        {
            var dense = DenseBlock(decoder.Dense, activation, h2, w2, "decoder");

            // SPConvTranspose2d: 周波数方向に 2 倍
            var up1 = Work("decoderUp1", h2 * w2 * 2 * C);
            Conv(dense, h2, w2, C, decoder.Up1, 0, up1, h2, w2, w2 * 2, 1, 1, 0, 1, ReUseKernels.Shuffle.Width, 2);
            Norm(up1, h2 * w2 * 2, decoder.Up1);

            // 公式は時間と周波数を入れ替えてから (1,3) で畳み込むので、ここでは (3,1) の畳み込みで時間方向に 4 倍
            var up2 = Work("decoderUp2", h2 * 4 * w2 * 2 * C);
            Conv(up1, h2, w2 * 2, C, decoder.Up2, 0, up2, h2, w2 * 2, w2 * 2, 1, 1, 1, 0, ReUseKernels.Shuffle.Height, 4);
            Norm(up2, h2 * 4 * w2 * 2, decoder.Up2);

            var output = Work(decoder.OutputName, h2 * 4 * w2 * 2 * decoder.Final.Cout);
            Conv(up2, h2 * 4, w2 * 2, C, decoder.Final, 0, output, h2 * 4, w2 * 2, w2 * 2, 1, 1, 0, 0, ReUseKernels.Shuffle.None, 1);
            return output;
        }

        /// <summary>
        /// DenseBlock。i 段目の畳み込みの入力は [x_i, x_{i-1}, …, x_0] の連結（新しいものが先）で、64 チャンネルずつ別のバッファから足し込む
        /// </summary>
        ReUseBuffer DenseBlock(ConvLayer[] layers, ReUseBuffer x0, int height, int width, string name)
        {
            var slices = new List<ReUseBuffer> { x0 };
            for (var i = 0; i < layers.Length; i++)
            {
                var output = Work($"{name}Dense{i}", height * width * C);
                var dilation = 1 << i;
                for (var j = 0; j <= i; j++)
                {
                    var source = slices[slices.Count - 1 - j];
                    Conv(source, height, width, C, layers[i], j, output, height, width, width, 1, 1, dilation, 1, ReUseKernels.Shuffle.None, 1,
                        dilationH: dilation, accumulate: j > 0);
                }
                Norm(output, height * width, layers[i]);
                slices.Add(output);
            }
            return slices[^1];
        }

        void Conv(
            ReUseBuffer input, int inHeight, int inWidth, int inStride,
            ConvLayer layer, int slice,
            ReUseBuffer output, int outHeight, int outWidth, int storeWidth,
            int strideH, int strideW, int padH, int padW,
            ReUseKernels.Shuffle shuffle, int factor,
            int dilationH = 1, bool accumulate = false)
            => ReUseKernels.Conv2d(
                device, input, inHeight, inWidth, inStride, layer.SliceChannels,
                layer.Weights[slice], accumulate ? null : layer.Bias, layer.Cout, layer.KernelH, layer.KernelW,
                output, outHeight, outWidth, storeWidth, strideH, strideW, dilationH, padH, padW, shuffle, factor, accumulate);

        void Norm(ReUseBuffer data, int count, ConvLayer layer)
            => ReUseKernels.InstanceNormPRelu(
                device, data, count, Work("normPartial", ReUseKernels.NormPartialLength(count)),
                layer.Gamma!, layer.Beta!, layer.Slope!, NormEpsilon);

        ReUseBuffer Work(string name, int length)
        {
            if (work.TryGetValue(name, out var buffer) && buffer.Length >= length)
                return buffer;
            buffer?.Dispose();
            buffer = device.CreateBuffer(length);
            work[name] = buffer;
            return buffer;
        }

        StftPlan GetPlan(int sampleRate)
        {
            if (plan?.SampleRate == sampleRate)
                return plan;
            plan?.Dispose();
            var (fft, hop) = GetStftSize(sampleRate);
            var bins = fft / 2 + 1;
            var window = CreateWindow(fft);

            // 逆変換（irfft）: x[n] = (1/N)(X_0 + X_{N/2}(-1)^n + 2 Σ (Re X_k cos − Im X_k sin))。DC と Nyquist の虚部は無視する。窓も掛けておく
            var inverse = new float[fft * bins * 2];
            for (var k = 0; k < bins; k++)
            {
                var scale = k == 0 || k == fft / 2 ? 1.0 : 2.0;
                for (var n = 0; n < fft; n++)
                {
                    var angle = 2 * Math.PI * ((long)k * n % fft) / fft;
                    var cos = Math.Cos(angle);
                    var sin = Math.Sin(angle);
                    inverse[n * bins * 2 + k] = (float)(window[n] * scale * cos / fft);
                    inverse[n * bins * 2 + bins + k] = k == 0 || k == fft / 2 ? 0 : (float)(-window[n] * scale * sin / fft);
                }
            }

            plan = new StftPlan(sampleRate, fft, hop, bins, device.CreateBuffer(inverse));
            return plan;
        }

        /// <summary>periodic Hann（torch.hann_window の既定）</summary>
        static double[] CreateWindow(int fft)
        {
            var window = new double[fft];
            for (var i = 0; i < fft; i++)
                window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / fft);
            return window;
        }

        Decoder LoadDecoder(IReadOnlyDictionary<string, SafeTensor> tensors, string prefix, string[] finals)
        {
            var dense = LoadDense(tensors, prefix + "dense_block.dense_block.");
            var up1 = LoadConv(tensors, prefix + "up_conv1.", C, false, convName: "0.conv.");
            var up2 = LoadConv(tensors, prefix + "up_conv2.", C, true, convName: "0.conv.");

            // 位相のデコーダーは実部と虚部の 1×1 畳み込みを 1 つの 2 チャンネルの畳み込みにまとめる
            var weights = new List<float>();
            var biases = new List<float>();
            foreach (var final in finals)
            {
                weights.AddRange(Get(tensors, prefix + final + "weight", 1, C, 1, 1).Data);
                biases.AddRange(Get(tensors, prefix + final + "bias", 1).Data);
            }
            var finalLayer = new ConvLayer(
                [Own(device.CreateBuffer(weights.ToArray()))], Own(device.CreateBuffer(biases.ToArray())),
                finals.Length, C, 1, 1, null, null, null);
            return new Decoder(dense, up1, up2, finalLayer, prefix + "output");
        }

        ConvLayer[] LoadDense(IReadOnlyDictionary<string, SafeTensor> tensors, string prefix)
        {
            var layers = new ConvLayer[DenseDepth];
            for (var i = 0; i < DenseDepth; i++)
                layers[i] = LoadConv(tensors, $"{prefix}{i}.", C, false);
            return layers;
        }

        /// <summary>
        /// Conv2d → InstanceNorm2d → PReLU の 1 組を読む。重みは入力 sliceChannels チャンネルごとに分け、[出力][行][列][入力] に並べ替える。
        /// swapKernel は公式が時間と周波数を入れ替えてから畳み込んでいる層（デコーダーの up_conv2）で、カーネルの行と列を入れ替える
        /// </summary>
        ConvLayer LoadConv(IReadOnlyDictionary<string, SafeTensor> tensors, string prefix, int sliceChannels, bool swapKernel, string convName = "0.")
        {
            if (!tensors.TryGetValue(prefix + convName + "weight", out var weight) || weight.Shape.Length != 4)
                throw new KeyNotFoundException($"重み {prefix}{convName}weight がありません。");
            var cout = weight.Shape[0];
            var cinTotal = weight.Shape[1];
            var kh = weight.Shape[2];
            var kw = weight.Shape[3];
            if (cinTotal % sliceChannels != 0)
                throw new ArgumentException($"重み {prefix}{convName}weight の入力チャンネル数 {cinTotal} が {sliceChannels} で割り切れません。");
            var outH = swapKernel ? kw : kh;
            var outW = swapKernel ? kh : kw;

            var slices = new ReUseBuffer[cinTotal / sliceChannels];
            for (var i = 0; i < slices.Length; i++)
                slices[i] = Own(device.CreateBuffer(ReUseKernels.ReorderConvWeight(weight.Data, cout, cinTotal, kh, kw, i * sliceChannels, sliceChannels, swapKernel)));

            var bias = Own(device.CreateBuffer(Get(tensors, prefix + convName + "bias", cout).Data));
            var gamma = Own(device.CreateBuffer(Get(tensors, prefix + "1.weight", C).Data));
            var beta = Own(device.CreateBuffer(Get(tensors, prefix + "1.bias", C).Data));
            var slope = Own(device.CreateBuffer(Get(tensors, prefix + "2.weight", C).Data));
            return new ConvLayer(slices, bias, cout, sliceChannels, outH, outW, gamma, beta, slope);
        }

        static SafeTensor Get(IReadOnlyDictionary<string, SafeTensor> tensors, string name, params int[] shape)
        {
            if (!tensors.TryGetValue(name, out var tensor))
                throw new KeyNotFoundException($"重み {name} がありません。");
            if (!tensor.Shape.AsSpan().SequenceEqual(shape))
                throw new ArgumentException($"重み {name} の形 [{string.Join(",", tensor.Shape)}] が想定 [{string.Join(",", shape)}] と違います。");
            return tensor;
        }

        ReUseBuffer Own(ReUseBuffer buffer)
        {
            owned.Add(buffer);
            return buffer;
        }

        /// <summary>作業用バッファを手放す。次の <see cref="Enhance"/> で作り直す</summary>
        public void ReleaseWork()
        {
            foreach (var buffer in work.Values)
                buffer.Dispose();
            work.Clear();
            mamba.ReleaseWork();
            // D3D11 は破棄したリソースのメモリーを Flush まで持ち続ける（48kHz・5 秒のチャンクの 2.4GB が残ったままになる）
            device.Run(context => context.Flush());
        }

        public void Dispose()
        {
            plan?.Dispose();
            foreach (var buffer in work.Values)
                buffer.Dispose();
            work.Clear();
            foreach (var item in owned)
                item.Dispose();
            owned.Clear();
        }

        sealed record ConvLayer(
            ReUseBuffer[] Weights, ReUseBuffer Bias, int Cout, int SliceChannels, int KernelH, int KernelW,
            ReUseBuffer? Gamma, ReUseBuffer? Beta, ReUseBuffer? Slope);

        sealed record Decoder(ConvLayer[] Dense, ConvLayer Up1, ConvLayer Up2, ConvLayer Final, string OutputName);

        /// <summary><see cref="Prepare"/> の結果。<see cref="Infer"/> と <see cref="Finish"/> に渡す</summary>
        internal sealed record PreparedInput(int SampleRate, int Fft, int Hop, double[] Window, int OriginalLength, int Frames, float[] ModelInput);

        sealed record StftPlan(int SampleRate, int Fft, int Hop, int Bins, ReUseBuffer Inverse) : IDisposable
        {
            public void Dispose() => Inverse.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using Vortice.DXGI;

namespace ReuseWinCli.Enhancement
{
    /// <summary>
    /// RE-USE の推論器。GPU の作業用バッファは推論ごとに使い回すので、GPU の処理は 1 つずつ行う。
    /// CPU の前処理と後処理はロックの外で行い、別のスレッドからの推論の GPU の処理と重ねられるようにする
    /// </summary>
    internal sealed class ReUseEngine : ISpeechEnhancer, IDisposable
    {
        /// <summary>公式の分割推論（inference_chunk.py）の長さ。GPU のメモリーが足りなければ短くする</summary>
        const double MaxChunkSeconds = 5;

        readonly Lock gate = new();
        readonly ReUseComputeDevice device;
        readonly ReUseModel model;
        readonly long bufferBudget;

        public bool IsWarp => device.IsWarp;

        public string AdapterName { get; }

        ReUseEngine(ReUseComputeDevice device, IReadOnlyDictionary<string, SafeTensor> tensors)
        {
            this.device = device;
            try
            {
                (bufferBudget, AdapterName) = GetBufferBudget(device);
                model = new ReUseModel(device, tensors);
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }

        /// <param name="modelPath">null なら exe に埋め込んだ重みを使う</param>
        public static ReUseEngine Create(string? modelPath, bool forceWarp)
        {
            var tensors = modelPath is null
                ? EmbeddedResources.Read("model.safetensors", bytes => SafeTensors.Read(bytes))
                : SafeTensors.Read(modelPath);
            return new ReUseEngine(ReUseComputeDevice.Create(forceWarp), tensors);
        }

        /// <summary>
        /// 1 回で処理するサンプル数（偶数）。いちばん大きい作業用バッファが D3D11 のリソースの上限と GPU のメモリーの予算に収まる長さにする
        /// </summary>
        public int GetChunkSamples(int sampleRate)
        {
            var (_, hop) = ReUseModel.GetStftSize(sampleRate);
            var samples = (int)(MaxChunkSeconds * sampleRate) / 2 * 2;
            while (samples > hop * 8 && ReUseModel.GetLargestBufferBytes(samples, sampleRate) > bufferBudget)
                samples = (samples - sampleRate / 10) / 2 * 2;
            return samples;
        }

        /// <summary>モノラルの音声を補正して、同じ長さで返す。別々のスレッドから同時に呼んでよい</summary>
        /// <param name="token">GPU の順番を待った後にも確かめる。WARP では 1 回の推論に数分かかるので、中止された後の推論を始めない</param>
        public float[] Enhance(ReadOnlySpan<float> samples, int sampleRate, CancellationToken token = default)
        {
            var prepared = ReUseModel.Prepare(samples, sampleRate);
            if (prepared is null)
                return [];
            float[] framed;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                framed = model.Infer(prepared);
            }
            return ReUseModel.Finish(prepared, framed);
        }

        /// <summary>
        /// 作業用バッファ 1 つあたりの上限。D3D11 のリソースの大きさの上限（max(128MB, VRAM の 1/4) と 2GB の小さい方）と、
        /// 構造化バッファのビューの要素数の上限（2^27）に収め、作業用バッファ全体（いちばん大きいものの 10 本分程度）が VRAM の 8 割を超えないようにする。
        /// https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-resources-limits
        /// </summary>
        static (long Budget, string AdapterName) GetBufferBudget(ReUseComputeDevice device)
        {
            const long megabyte = 1024 * 1024;
            long dedicated = 0;
            var name = "";
            using (var dxgiDevice = device.Device.QueryInterfaceOrNull<IDXGIDevice>())
            {
                if (dxgiDevice is not null)
                {
                    using var adapter = dxgiDevice.GetAdapter();
                    var description = adapter.Description;
                    // PointerUSize を long に直接変換すると uint を経由し、4GB 以上の VRAM で桁あふれする
                    dedicated = (long)(nuint)description.DedicatedVideoMemory;
                    name = description.Description;
                }
            }
            var resourceLimit = Math.Min(Math.Max(128 * megabyte, dedicated / 4), 2048 * megabyte);
            var viewLimit = (1L << 27) * sizeof(float);
            return (Math.Min(Math.Min(resourceLimit, viewLimit), Math.Max(128 * megabyte, dedicated / 12)), name);
        }

        public void Dispose()
        {
            lock (gate)
            {
                model.Dispose();
                device.Dispose();
            }
        }
    }
}

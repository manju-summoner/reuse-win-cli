using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace ReuseWinCli.Enhancement
{
    /// <summary>
    /// RE-USE の推論に使う D3D11 デバイス。GPU で作れなければ WARP（ソフトウェアの D3D11）で作る。
    /// </summary>
    internal sealed class ReUseComputeDevice : IDisposable
    {
        const int MaxResources = 7;
        const int ConstantBufferBytes = 128;

        readonly Lock gate = new();
        readonly DisposeCollector disposer = new();
        readonly Dictionary<string, ID3D11ComputeShader> shaders = [];
        readonly ID3D11ShaderResourceView?[] resourceSlots = new ID3D11ShaderResourceView?[MaxResources];
        readonly ID3D11UnorderedAccessView?[] targetSlots = new ID3D11UnorderedAccessView?[2];
        readonly ID3D11Buffer constants;
        bool disposed;

        public ID3D11Device Device { get; }
        public ID3D11DeviceContext Context { get; }
        public bool IsWarp { get; }

        /// <summary>
        /// Dispatch の直後に、デバイスのロックの中でシェーダーの名前とともに呼ぶ。カーネルごとの GPU の時間をタイムスタンプのクエリで測るテストが使う
        /// </summary>
        internal Action<ID3D11DeviceContext, string>? DispatchObserver { get; set; }

        ReUseComputeDevice(ID3D11Device device, ID3D11DeviceContext context, bool isWarp)
        {
            Device = device;
            Context = context;
            IsWarp = isWarp;
            disposer.Collect(context);
            disposer.Collect(device);
            constants = device.CreateBuffer(new BufferDescription(
                ConstantBufferBytes,
                BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write,
                ResourceOptionFlags.None,
                0), (SubresourceData?)null);
            disposer.Collect(constants);
        }

        public static ReUseComputeDevice Create(bool forceWarp = false)
        {
            // cs_5_0 と構造化バッファは機能レベル 11_0 から。
            // https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-devices-downlevel-intro
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            if (!forceWarp
                && D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, levels, out ID3D11Device device, out ID3D11DeviceContext context).Success)
                return new ReUseComputeDevice(device, context, false);

            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None, levels, out device, out context).CheckError();
            return new ReUseComputeDevice(device, context, true);
        }

        public ReUseBuffer CreateBuffer(int length) => new(this, length, null);

        public ReUseBuffer CreateBuffer(ReadOnlySpan<float> data) => new(this, data.Length, data);

        public unsafe void Dispatch<TConstants>(
            string shaderName,
            in TConstants values,
            int groupsX,
            int groupsY,
            ReadOnlySpan<ReUseBuffer?> resources,
            ReUseBuffer target,
            ReUseBuffer? secondTarget = null)
            where TConstants : unmanaged
        {
            if (Unsafe.SizeOf<TConstants>() > ConstantBufferBytes)
                throw new ArgumentException("定数が定数バッファに収まりません。", nameof(values));
            if (resources.Length > MaxResources)
                throw new ArgumentException("入力が多すぎます。", nameof(resources));
            // 1 次元あたりのグループ数の上限
            // https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-dispatch
            if (groupsX is <= 0 or > 65535 || groupsY is <= 0 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(groupsX), $"グループ数 {groupsX}×{groupsY} は Dispatch の上限を超えています。");

            var shader = GetShader(shaderName);
            lock (gate)
            {
                var mapped = Context.Map(constants, 0, MapMode.WriteDiscard, MapFlags.None);
                try
                {
                    var destination = new Span<byte>((void*)mapped.DataPointer, ConstantBufferBytes);
                    destination.Clear();
                    MemoryMarshal.Write(destination, in values);
                }
                finally
                {
                    Context.Unmap(constants, 0);
                }

                for (var i = 0; i < resources.Length; i++)
                    resourceSlots[i] = resources[i]?.Srv;
                targetSlots[0] = target.Uav;
                targetSlots[1] = secondTarget?.Uav;

                Context.CSSetShader(shader);
                Context.CSSetConstantBuffer(0, constants);
                Context.CSSetShaderResources(0, (uint)resources.Length, resourceSlots!);
                Context.CSSetUnorderedAccessViews(0, (uint)targetSlots.Length, targetSlots!);
                Context.Dispatch((uint)groupsX, (uint)groupsY, 1);
                DispatchObserver?.Invoke(Context, shaderName);

                // 次の Dispatch で出力を入力に回すので、束縛を外しておく（同じバッファを入力と出力に同時に束縛すると入力が外される）
                Array.Clear(resourceSlots);
                Array.Clear(targetSlots);
                Context.CSSetShaderResources(0, (uint)resources.Length, resourceSlots!);
                Context.CSSetUnorderedAccessViews(0, (uint)targetSlots.Length, targetSlots!);
            }
        }

        internal void Run(Action<ID3D11DeviceContext> action)
        {
            lock (gate)
                action(Context);
        }

        ID3D11ComputeShader GetShader(string name)
        {
            lock (gate)
            {
                if (shaders.TryGetValue(name, out var cached))
                    return cached;
            }

            var bytes = EmbeddedResources.ReadAllBytes($"Shaders/{name}.cso");
            lock (gate)
            {
                if (shaders.TryGetValue(name, out var cached))
                    return cached;
                var shader = Device.CreateComputeShader(bytes, null);
                disposer.Collect(shader);
                shaders[name] = shader;
                return shader;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                shaders.Clear();
                Context.ClearState();
                disposer.DisposeAndClear();
                disposed = true;
            }
        }
    }

    /// <summary>
    /// float の構造化バッファ。D3D11 のリソースの大きさの上限（GPU によっては 128MB）を超えないよう、呼び出し側で長さを抑える。
    /// https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-resources-limits
    /// </summary>
    internal sealed class ReUseBuffer : IDisposable
    {
        readonly ReUseComputeDevice device;
        readonly DisposeCollector disposer = new();
        readonly ID3D11Buffer buffer;
        ID3D11Buffer? staging;
        bool disposed;

        public int Length { get; }
        public ID3D11ShaderResourceView Srv { get; }
        public ID3D11UnorderedAccessView Uav { get; }

        internal unsafe ReUseBuffer(ReUseComputeDevice device, int length, ReadOnlySpan<float> initial)
        {
            this.device = device;
            Length = length;
            var description = new BufferDescription(
                (uint)(sizeof(float) * length),
                BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.BufferStructured,
                sizeof(float));
            fixed (float* pointer = initial)
            {
                buffer = initial.IsEmpty
                    ? device.Device.CreateBuffer(description, (SubresourceData?)null)
                    : device.Device.CreateBuffer(description, new SubresourceData((nint)pointer));
            }
            disposer.Collect(buffer);
            Srv = device.Device.CreateShaderResourceView(buffer, new ShaderResourceViewDescription(buffer, Format.Unknown, 0, (uint)length));
            disposer.Collect(Srv);
            Uav = device.Device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription(buffer, Format.Unknown, 0, (uint)length));
            disposer.Collect(Uav);
        }

        public unsafe void Upload(ReadOnlySpan<float> source)
        {
            var count = Math.Min(source.Length, Length);
            if (count <= 0)
                return;
            fixed (float* pointer = source)
            {
                var address = (nint)pointer;
                device.Run(context => context.UpdateSubresource(buffer, 0, new Box(0, 0, 0, sizeof(float) * count, 1, 1), address, 0, 0));
            }
        }

        public unsafe void Readback(Span<float> destination)
        {
            var count = Math.Min(destination.Length, Length);
            if (count <= 0)
                return;
            if (staging is null)
            {
                staging = device.Device.CreateBuffer(new BufferDescription(
                    (uint)(sizeof(float) * Length),
                    BindFlags.None,
                    ResourceUsage.Staging,
                    CpuAccessFlags.Read,
                    ResourceOptionFlags.BufferStructured,
                    sizeof(float)), (SubresourceData?)null);
                disposer.Collect(staging);
            }

            var target = staging;
            var result = new float[count];
            device.Run(context =>
            {
                context.CopyResource(target, buffer);
                var mapped = context.Map(target, 0, MapMode.Read, MapFlags.None);
                try
                {
                    new ReadOnlySpan<float>((void*)mapped.DataPointer, count).CopyTo(result);
                }
                finally
                {
                    context.Unmap(target, 0);
                }
            });
            result.CopyTo(destination);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposer.DisposeAndClear();
            disposed = true;
        }
    }
}

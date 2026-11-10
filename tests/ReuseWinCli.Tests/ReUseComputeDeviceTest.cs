using NUnit.Framework;
using ReuseWinCli.Enhancement;
using Vortice.DXGI;

namespace ReuseWinCli.Tests
{
    /// <summary>
    /// 推論のデバイスを、デバイスを作成できる GPU のうち、最も性能の高いもので作成する（ハイブリッド GPU のノート PC で内蔵 GPU を使わない）。
    /// GPU が 1 つの PC では、既定のアダプターと同じものになるので、取り違えてもテストは失敗しない
    /// </summary>
    class ReUseComputeDeviceTest
    {
        [Test]
        public void DeviceUsesFastestUsableAdapter()
        {
            var adapters = ReUseComputeDevice.GetHardwareAdaptersByPerformance();
            Assume.That(adapters, Is.Not.Null, "IDXGIFactory6 がありません");
            Vortice.Luid? expected = null;
            try
            {
                foreach (var candidate in adapters!)
                {
                    if (!ReUseComputeDevice.TryCreateDevice(candidate, out var probe, out var probeContext))
                        continue;
                    probeContext.Dispose();
                    probe.Dispose();
                    expected = candidate.Description1.Luid;
                    break;
                }
            }
            finally
            {
                foreach (var adapter in adapters!)
                    adapter.Dispose();
            }

            using var device = ReUseComputeDevice.Create();
            if (expected is null)
            {
                // デバイスを作成できるハードウェアの GPU が無い（GitHub Actions のランナーなど）
                Assert.That(device.IsWarp, Is.True);
                return;
            }

            using var dxgi = device.Device.QueryInterface<IDXGIDevice>();
            using var deviceAdapter = dxgi.GetAdapter();
            using var actual = deviceAdapter.QueryInterface<IDXGIAdapter1>();
            TestContext.Out.WriteLine($"actual {actual.Description1.Description}");
            Assert.That(device.IsWarp, Is.False);
            Assert.That(actual.Description1.Luid, Is.EqualTo(expected.Value));
            // 期待値は実装と同じ列挙方法で求めているので、ソフトウェアアダプター（Basic Render Driver）を除外できているかは別に確認する
            Assert.That(actual.Description1.Flags & AdapterFlags.Software, Is.EqualTo(AdapterFlags.None));
        }
    }
}

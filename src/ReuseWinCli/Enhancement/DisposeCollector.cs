using System;
using System.Collections.Generic;

namespace ReuseWinCli.Enhancement
{
    /// <summary>
    /// 作った順と逆の順に破棄する。D3D11 のビューを元のバッファより先に、デバイスを最後に手放すため
    /// </summary>
    internal sealed class DisposeCollector
    {
        readonly List<IDisposable> items = [];

        public void Collect(IDisposable item) => items.Add(item);

        public void DisposeAndClear()
        {
            for (var i = items.Count - 1; i >= 0; i--)
                items[i].Dispose();
            items.Clear();
        }
    }
}

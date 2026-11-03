using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ReuseWinCli.Enhancement
{
    internal sealed record SafeTensor(int[] Shape, float[] Data);

    /// <summary>
    /// safetensors 形式（先頭 8 バイトがヘッダーの長さ、続いて JSON のヘッダー、その後ろにテンソルの生データ）を読む。F32 のテンソルだけを扱う。
    /// https://github.com/huggingface/safetensors#format
    /// </summary>
    internal static class SafeTensors
    {
        public static Dictionary<string, SafeTensor> Read(string path) => Read(File.ReadAllBytes(path));

        public static Dictionary<string, SafeTensor> Read(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 8)
                throw new InvalidDataException("safetensors のヘッダーがありません。");

            var headerLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            if (headerLength > (ulong)(bytes.Length - 8))
                throw new InvalidDataException("safetensors のヘッダーの長さが不正です。");

            var dataStart = 8 + (int)headerLength;
            using var header = JsonDocument.Parse(bytes.Slice(8, (int)headerLength).ToArray());
            var tensors = new Dictionary<string, SafeTensor>();
            foreach (var entry in header.RootElement.EnumerateObject())
            {
                if (entry.Name == "__metadata__")
                    continue;

                var dtype = entry.Value.GetProperty("dtype").GetString();
                if (dtype != "F32")
                    throw new InvalidDataException($"{entry.Name} の型 {dtype} には対応していません。");

                var shapeElement = entry.Value.GetProperty("shape");
                var shape = new int[shapeElement.GetArrayLength()];
                var count = 1L;
                for (var i = 0; i < shape.Length; i++)
                {
                    shape[i] = shapeElement[i].GetInt32();
                    count *= shape[i];
                }

                var offsets = entry.Value.GetProperty("data_offsets");
                var begin = offsets[0].GetInt64();
                var end = offsets[1].GetInt64();
                if (begin < 0 || end < begin || dataStart + end > bytes.Length || end - begin != count * sizeof(float))
                    throw new InvalidDataException($"{entry.Name} のデータの範囲が不正です。");

                var data = new float[count];
                MemoryMarshal.Cast<byte, float>(bytes.Slice(dataStart + (int)begin, (int)(end - begin))).CopyTo(data);
                tensors[entry.Name] = new SafeTensor(shape, data);
            }
            return tensors;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace UniversalUmap.Rendering.Core;

public unsafe class ByteString : IDisposable
{
    public IntPtr Pointer { get; }

    public ByteString(string value)
    {
        Pointer = Marshal.StringToHGlobalAnsi(value);
    }

    public void Dispose()
    {
        Marshal.FreeHGlobal(Pointer);
    }

    public static implicit operator byte*(ByteString value) => (byte*)value.Pointer;
}

public unsafe class ByteStringList : IDisposable
{
    private readonly List<ByteString> inner;
    private readonly byte** ptr;

    public ByteStringList(IEnumerable<string> values)
    {
        inner = values.Select(static x => new ByteString(x)).ToList();
        ptr = (byte**)Marshal.AllocHGlobal(IntPtr.Size * inner.Count + 1);
        for (var i = 0; i < inner.Count; i++)
            ptr[i] = (byte*)inner[i].Pointer;
    }

    public uint UCount => (uint)inner.Count;

    public void Dispose()
    {
        foreach (var value in inner)
            value.Dispose();
        Marshal.FreeHGlobal((IntPtr)ptr);
    }

    public static implicit operator byte**(ByteStringList value) => value.ptr;
}

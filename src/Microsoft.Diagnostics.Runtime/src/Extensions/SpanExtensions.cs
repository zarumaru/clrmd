﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Microsoft.Diagnostics.Runtime
{
    internal static class SpanExtensions
    {
        public static int Read(this Stream stream, Span<byte> span)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(span.Length);
            try
            {
                int numRead = stream.Read(buffer, 0, span.Length);
                new ReadOnlySpan<byte>(buffer, 0, numRead).CopyTo(span);
                return numRead;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static unsafe ulong AsPointer(this Span<byte> span)
        {
            DebugOnly.Assert(span.Length >= IntPtr.Size);
            DebugOnly.Assert((int)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span)) % IntPtr.Size == 0);
            return IntPtr.Size == 4
                ? Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(span))
                : Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(span));
        }

        public static unsafe int AsInt32(this Span<byte> span)
        {
            DebugOnly.Assert(span.Length >= sizeof(int));
            DebugOnly.Assert((int)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span)) % sizeof(int) == 0);
            return Unsafe.As<byte, int>(ref MemoryMarshal.GetReference(span));
        }

        public static unsafe uint AsUInt32(this Span<byte> span)
        {
            DebugOnly.Assert(span.Length >= sizeof(uint));
            DebugOnly.Assert((int)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span)) % sizeof(uint) == 0);
            return Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(span));
        }
    }
}

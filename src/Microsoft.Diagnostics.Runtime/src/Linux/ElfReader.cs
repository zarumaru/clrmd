﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Diagnostics.Runtime.Linux
{
    internal unsafe class Reader
    {
        public IAddressSpace DataSource { get; }

        public Reader(IAddressSpace source)
        {
            DataSource = source;
        }

        public T? TryRead<T>(long position)
            where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            T result;
            int read = DataSource.Read(position, new Span<byte>(&result, size));
            if (read == size)
                return result;

            return null;
        }

        public T Read<T>(long position)
            where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            T result;
            DataSource.Read(position, new Span<byte>(&result, size));
            return result;
        }

        public T Read<T>(ref long position)
            where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            T result;
            int read = DataSource.Read(position, new Span<byte>(&result, size));
            if (read != size)
                throw new IOException();

            position += read;
            return result;
        }

        public int ReadBytes(long position, Span<byte> buffer) => DataSource.Read(position, buffer);

        public string ReadNullTerminatedAscii(long position)
        {
            StringBuilder builder = new StringBuilder(64);
            Span<byte> bytes = stackalloc byte[64];

            bool done = false;
            int read;
            while (!done && (read = DataSource.Read(position, bytes)) != 0)
            {
                for (int i = 0; !done && i < read; i++)
                {
                    if (bytes[i] != 0)
                        builder.Append((char)bytes[i]);
                    else
                        done = true;
                }
            }

            return builder.ToString();
        }

        public string ReadNullTerminatedAscii(long position, int length)
        {
            byte[]? array = null;
            Span<byte> buffer = length <= 32 ? stackalloc byte[length] : (array = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);

            try
            {
                int read = DataSource.Read(position, buffer);
                if (read == 0)
                    return string.Empty;

                if (buffer[read - 1] == '\0')
                    read--;

#if NETCOREAPP
                return Encoding.ASCII.GetString(buffer.Slice(0, read));
#else
                fixed (byte* bufferPtr = buffer)
                    return Encoding.ASCII.GetString(bufferPtr, read);
#endif
            }
            finally
            {
                if (array != null)
                    ArrayPool<byte>.Shared.Return(array);
            }
        }
    }
}
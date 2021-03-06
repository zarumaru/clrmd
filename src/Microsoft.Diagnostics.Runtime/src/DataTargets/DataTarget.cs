﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime.Linux;
using Microsoft.Diagnostics.Runtime.Utilities;

// TODO: remove this after code is cleaned up

#pragma warning disable CA2000 // Dispose objects before losing scope

namespace Microsoft.Diagnostics.Runtime
{
    /// <summary>
    /// A crash dump or live process to read out of.
    /// </summary>
    public sealed class DataTarget : IDisposable
    {
        private IBinaryLocator? _locator;
        private bool _disposed;
        private ImmutableArray<ClrInfo> _clrs;
        private ModuleInfo[]? _modules;
        private readonly Dictionary<string, PEImage?> _pefileCache = new Dictionary<string, PEImage?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the data reader for this instance.
        /// </summary>
        public IDataReader DataReader { get; }

        public CacheOptions CacheOptions { get; } = new CacheOptions();

        /// <summary>
        /// Gets or sets instance to manage the symbol path(s).
        /// </summary>
        public IBinaryLocator BinaryLocator
        {
            get
            {
                if (_locator is null)
                {
                    string symPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
                    _locator = new Implementation.SymbolServerLocator(symPath);

                }

                return _locator;
            }

            set => _locator = value;
        }

        /// <summary>
        /// Creates a DataTarget from the given reader.
        /// </summary>
        /// <param name="reader">The data reader to use.</param>
        public DataTarget(IDataReader reader)
        {
            DataReader = reader ?? throw new ArgumentNullException(nameof(reader));

            DebugOnlyLoadLazyValues();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                DataReader.Dispose();

                lock (_pefileCache)
                {
                    foreach (PEImage? img in _pefileCache.Values)
                        img?.Stream.Dispose();

                    _pefileCache.Clear();
                }

                _disposed = true;
            }
        }

        internal PEImage? LoadPEImage(string fileName)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DataTarget));

            if (string.IsNullOrEmpty(fileName))
                return null;

            PEImage? result;

            lock (_pefileCache)
            {
                if (_pefileCache.TryGetValue(fileName, out result))
                    return result;
            }

            Stream stream = File.OpenRead(fileName);
            result = new PEImage(stream);

            if (!result.IsValid)
            {
                stream.Dispose();
                result = null;
            }

            lock (_pefileCache)
            {
                // We may have raced with another thread and that thread put a value here first
                if (_pefileCache.TryGetValue(fileName, out PEImage? cached) && cached != null)
                {
                    result?.Dispose(); // We don't need this instance now.
                    return cached;
                }

                return _pefileCache[fileName] = result;
            }
        }

        [Conditional("DEBUG")]
        private void DebugOnlyLoadLazyValues()
        {
            // Prefetch these values in debug builds for easier debugging
            GetOrCreateClrVersions();
            EnumerateModules();
        }

        /// <summary>
        /// Gets the list of CLR versions loaded into the process.
        /// </summary>
        public ImmutableArray<ClrInfo> ClrVersions => GetOrCreateClrVersions();

        private ImmutableArray<ClrInfo> GetOrCreateClrVersions()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DataTarget));

            if (!_clrs.IsDefault)
                return _clrs;

            Architecture arch = DataReader.Architecture;
            ImmutableArray<ClrInfo>.Builder versions = ImmutableArray.CreateBuilder<ClrInfo>(2);
            foreach (ModuleInfo module in EnumerateModules())
            {
                if (!ClrInfoProvider.IsSupportedRuntime(module, out var flavor, out var platform))
                    continue;

                string dacFileName = ClrInfoProvider.GetDacFileName(flavor, platform);
                string? dacLocation = Path.Combine(Path.GetDirectoryName(module.FileName), dacFileName);

                if (platform == Platform.Linux)
                {
                    if (File.Exists(dacLocation))
                    {
                        // Works around issue https://github.com/dotnet/coreclr/issues/20205
                        int processId = Process.GetCurrentProcess().Id;
                        string tempDirectory = Path.Combine(Path.GetTempPath(), "clrmd" + processId);
                        Directory.CreateDirectory(tempDirectory);

                        string symlink = Path.Combine(tempDirectory, dacFileName);
                        if (LinuxFunctions.symlink(dacLocation, symlink) == 0)
                        {
                            dacLocation = symlink;
                        }
                    }
                    else
                    {
                        dacLocation = dacFileName;
                    }
                }
                else if (!File.Exists(dacLocation) || !PlatformFunctions.IsEqualFileVersion(dacLocation, module.Version))
                {
                    dacLocation = null;
                }

                VersionInfo version = module.Version;
                string dacAgnosticName = ClrInfoProvider.GetDacRequestFileName(flavor, arch, arch, version, platform);
                string dacRegularName = ClrInfoProvider.GetDacRequestFileName(flavor, IntPtr.Size == 4 ? Architecture.X86 : Architecture.Amd64, arch, version, platform);

                DacInfo dacInfo = new DacInfo(DataReader, dacAgnosticName, arch, 0, module.FileSize, module.TimeStamp, dacRegularName, module.Version);
                versions.Add(new ClrInfo(this, flavor, module, dacInfo, dacLocation));
            }

            _clrs = versions.MoveOrCopyToImmutable();
            return _clrs;
        }

        /// <summary>
        /// Enumerates information about the loaded modules in the process (both managed and unmanaged).
        /// </summary>
        public IEnumerable<ModuleInfo> EnumerateModules()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DataTarget));

            if (_modules != null)
                return _modules;

            char[] invalid = Path.GetInvalidPathChars();
            _modules = DataReader.EnumerateModules().Where(m => m.FileName != null && m.FileName.IndexOfAny(invalid) < 0).ToArray();
            Array.Sort(_modules, (a, b) => a.ImageBase.CompareTo(b.ImageBase));
            return _modules;
        }

        #region Statics
        /// <summary>
        /// Gets a set of helper functions that are consistently implemented across all platforms.
        /// </summary>
        public static PlatformFunctions PlatformFunctions { get; } =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? (PlatformFunctions)new LinuxFunctions() : new WindowsFunctions();

        /// <summary>
        /// Creates a DataTarget from a crash dump.
        /// This method is only supported on Windows.
        /// </summary>
        /// <param name="fileName">The crash dump's file name.</param>
        /// <returns>A DataTarget instance.</returns>
        /// <exception cref="InvalidDataException">
        /// The file specified by <paramref name="fileName"/> is not a crash dump.
        /// </exception>
        /// <exception cref="PlatformNotSupportedException">
        /// The current platform is not Windows.
        /// </exception>
        public static DataTarget LoadCrashDump(string fileName)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                ThrowPlatformNotSupportedException();

            return new DataTarget(new DbgEngDataReader(fileName));
        }

        /// <summary>
        /// Creates a DataTarget from a coredump.
        /// This method is only supported on Linux.
        /// </summary>
        /// <param name="fileName">The path to a core dump.</param>
        /// <returns>A DataTarget instance.</returns>
        /// <exception cref="InvalidDataException">
        /// The file specified by <paramref name="fileName"/> is not a coredump.
        /// </exception>
        /// <exception cref="PlatformNotSupportedException">
        /// The current platform is not Linux.
        /// </exception>
        public static DataTarget LoadCoreDump(string fileName)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                ThrowPlatformNotSupportedException();

            CoreDumpReader reader = new CoreDumpReader(fileName);
            return new DataTarget(reader)
            {
                BinaryLocator = new LinuxDefaultSymbolLocator(reader.GetModulesFullPath())
            };
        }

        /// <summary>
        /// Passively attaches to a live process.  Note that this method assumes that you have alread suspended
        /// the target process.  It is unsupported to inspect a running process.
        /// </summary>
        /// <param name="processId">The ID of the process to attach to.</param>
        /// <returns>A DataTarget instance.</returns>
        public static DataTarget PassiveAttachToProcess(int processId)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                LinuxLiveDataReader reader = new LinuxLiveDataReader(processId, suspend: false);
                return new DataTarget(reader)
                {
                    BinaryLocator = new LinuxDefaultSymbolLocator(reader.GetModulesFullPath())
                };
            }

            return new DataTarget(new LiveDataReader(processId, createSnapshot: false));
        }

        /// <summary>
        /// Attaches to a live process.
        /// </summary>
        /// <param name="processId">The ID of the process to suspend and attach to.</param>
        /// <returns>A DataTarget instance.</returns>
        /// <exception cref="ArgumentException">
        /// The process specified by <paramref name="processId"/> is not running.
        /// </exception>
        public static DataTarget SuspendAndAttachToProcess(int processId)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                LinuxLiveDataReader reader = new LinuxLiveDataReader(processId, suspend: true);
                return new DataTarget(reader)
                {
                    BinaryLocator = new LinuxDefaultSymbolLocator(reader.GetModulesFullPath())
                };
            }

            return new DataTarget(new DbgEngDataReader(processId, invasive: false, 5000));
        }

        /// <summary>
        /// Attaches to a snapshot process (see https://docs.microsoft.com/windows/win32/api/_proc_snap/).
        /// This method is only supported on Windows.
        /// </summary>
        /// <param name="processId">The ID of the process to attach to.</param>
        /// <returns>A DataTarget instance.</returns>
        /// <exception cref="ArgumentException">
        /// The process specified by <paramref name="processId"/> is not running.
        /// </exception>
        /// <exception cref="PlatformNotSupportedException">
        /// The current platform is not Windows.
        /// </exception>
        public static DataTarget CreateSnapshotAndAttach(int processId)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                ThrowPlatformNotSupportedException();

            return new DataTarget(new LiveDataReader(processId, createSnapshot: true));
        }

        [DoesNotReturn]
        private static void ThrowPlatformNotSupportedException() =>
            throw new PlatformNotSupportedException("This method is not supported on this platform.");
        #endregion
    }
}

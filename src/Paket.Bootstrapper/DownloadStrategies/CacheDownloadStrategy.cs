﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using Paket.Bootstrapper.HelperProxies;

namespace Paket.Bootstrapper.DownloadStrategies
{
    internal class CacheDownloadStrategy : DownloadStrategy, IHaveEffectiveStrategy
    {
        public override string Name { get { return String.Format("{0} - cached", EffectiveStrategy.Name); } }

        public override bool CanDownloadHashFile
        {
            get { return EffectiveStrategy.CanDownloadHashFile; }
        }

        private readonly string _paketCacheDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NuGet", "Cache", "Paket");

        public IDownloadStrategy EffectiveStrategy { get; set; }
        public IFileSystemProxy FileSystemProxy { get; set; }

        public CacheDownloadStrategy(IDownloadStrategy effectiveStrategy, IFileSystemProxy fileSystemProxy)
        {
            if (effectiveStrategy == null)
                throw new ArgumentException("CacheDownloadStrategy needs a non-null effective strategy");
            if (effectiveStrategy.FallbackStrategy != null)
                throw new ArgumentException("CacheDownloadStrategy should not have a fallback strategy");

            EffectiveStrategy = effectiveStrategy;
            FileSystemProxy = fileSystemProxy;
        }


        protected override string GetLatestVersionCore(bool ignorePrerelease)
        {
            try
            {
                return EffectiveStrategy.GetLatestVersion(ignorePrerelease);
            }
            catch (WebException)
            {
                if (FallbackStrategy == null)
                {
                    var latestVersion = GetLatestVersionInCache(ignorePrerelease);

                    ConsoleImpl.WriteInfo("Unable to look up the latest version online, the cache contains version {0}.", latestVersion);

                    return latestVersion;
                }
                throw;
            }
        }

        protected override void DownloadVersionCore(string latestVersion, string target, PaketHashFile hashFile)
        {
            var cached = Path.Combine(_paketCacheDir, latestVersion, "paket.exe");

            if (!FileSystemProxy.FileExists(cached))
            {
                ConsoleImpl.WriteInfo("Version {0} not found in cache.", latestVersion);

                DownloadAndPlaceInCache(latestVersion, target, cached, hashFile);
                return;
            }

            FileSystemProxy.WaitForFileFinished(cached);

            if (!BootstrapperHelper.ValidateHash(FileSystemProxy, hashFile, latestVersion, cached))
            {
                ConsoleImpl.WriteWarning("Version {0} found in cache but it's hashFile isn't valid.", latestVersion);

                DownloadAndPlaceInCache(latestVersion, target, cached, hashFile);
                return;
            }

            ConsoleImpl.WriteInfo("Copying version {0} from cache.", latestVersion);
            ConsoleImpl.WriteTrace("{0} -> {1}", cached, target);
            try
            {
                using (var targetStream = FileSystemProxy.CreateExclusive(target))
                using (var cachedStream = FileSystemProxy.OpenRead(cached))
                {
                    cachedStream.CopyTo(targetStream);
                }
            }
            catch
            {
                DeleteIfPossible(target);
                throw;
            }
        }

        private void DownloadAndPlaceInCache(string latestVersion, string target, string cached, PaketHashFile hashFile)
        {
            FileSystemProxy.CreateDirectory(Path.GetDirectoryName(cached));

            try
            {
                using (var targetStream = FileSystemProxy.CreateExclusive(target))
                using (var cachedStream = FileSystemProxy.CreateExclusive(cached))
                {
                    var tempFile = Path.Combine(FileSystemProxy.GetTempPath(), Guid.NewGuid().ToString());

                    EffectiveStrategy.DownloadVersion(latestVersion, tempFile, hashFile);

                    if (!BootstrapperHelper.ValidateHash(FileSystemProxy, hashFile, latestVersion, tempFile))
                    {
                        throw new InvalidOperationException(
                            string.Format("paket.exe was currupted after download by {0}: Invalid hash",
                                EffectiveStrategy.Name));
                    }

                    ConsoleImpl.WriteTrace("Caching version {0} for later, hash is ok", latestVersion);
                    using (var tempStream = FileSystemProxy.OpenRead(tempFile))
                    {
                        tempStream.CopyTo(targetStream);
                        tempStream.Seek(0, SeekOrigin.Begin);
                        tempStream.CopyTo(cachedStream);
                    }

                    FileSystemProxy.DeleteFile(tempFile);
                }
            }
            catch
            {
                DeleteIfPossible(target);
                DeleteIfPossible(cached);
                throw;
            }
        }

        private void DeleteIfPossible(string path)
        {
            try
            {
                FileSystemProxy.DeleteFile(path);
            }
            catch
            {
                // IGNORE
            }
        }

        protected override PaketHashFile DownloadHashFileCore(string latestVersion)
        {
            if (!EffectiveStrategy.CanDownloadHashFile)
            {
                return null;
            }

            var cachedPath = GetHashFilePathInCache(latestVersion);

            if (File.Exists(cachedPath))
            {
                // Maybe there's another bootstraper process running
                // We trust it to close the file with the correct content
                FileSystemProxy.WaitForFileFinished(cachedPath);
                ConsoleImpl.WriteInfo("Hash file of version {0} found in cache.", latestVersion);
            }
            else
            {
                FileSystemProxy.CreateDirectory(Path.GetDirectoryName(cachedPath));
                try
                {
                    using (var finalStream = FileSystemProxy.CreateExclusive(cachedPath))
                    {
                        ConsoleImpl.WriteInfo("Hash file of version {0} not found in cache.", latestVersion);
                        var hashFile = EffectiveStrategy.DownloadHashFile(latestVersion);
                        Debug.Assert(hashFile != null,
                            "'EffectiveStrategy.CanDownloadHashFile' but DownloadHashFile returned null");

                        ConsoleImpl.WriteTrace("Writing hashFile file in cache.");
                        ConsoleImpl.WriteTrace("hashFile -> {0}", cachedPath);
                        hashFile.WriteToStream(finalStream);
                        return hashFile;
                    }
                }
                catch (IOException ex)
                {
                    if (ex.HResult == HelperProxies.FileSystemProxy.HRESULT_ERROR_SHARING_VIOLATION)
                    {
                        ConsoleImpl.WriteTrace("Can't lock hashFile file, another instance might be writing it. Waiting.");
                        // Same as before let's trust other bootstraper processes
                        FileSystemProxy.WaitForFileFinished(cachedPath);
                    }
                    else
                    {
                        DeleteIfPossible(cachedPath);
                        throw;
                    }
                }
                catch
                {
                    DeleteIfPossible(cachedPath);
                    throw;
                }
            }

            return PaketHashFile.FromStrings(FileSystemProxy.ReadAllLines(cachedPath));
        }

        protected override void SelfUpdateCore(string latestVersion)
        {
            EffectiveStrategy.SelfUpdate(latestVersion);
        }

        private string GetLatestVersionInCache(bool ignorePrerelease)
        {
            FileSystemProxy.CreateDirectory(_paketCacheDir);
            var zero = new SemVer();

            return FileSystemProxy.GetDirectories(_paketCacheDir)
                .Select(Path.GetFileName)
                .OrderByDescending(x =>
                {
                    try
                    {
                        var version = SemVer.Create(x);

                        if (ignorePrerelease && version.PreRelease != null)
                            return zero;
                        else
                            return version;
                    }
                    catch (Exception)
                    {
                        return zero;
                    }
                })
                .FirstOrDefault() ?? "0";
        }

        public string GetHashFilePathInCache(string version)
        {
            return Path.Combine(_paketCacheDir, version, "paket-sha256.txt");
        }
    }
}
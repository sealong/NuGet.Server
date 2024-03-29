﻿using Ninject;
using NuGet.Resources;
using NuGet.Server.DataServices;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Web.Configuration;

namespace NuGet.Server.Infrastructure
{
    /// <summary>
    /// ServerPackageRepository represents a folder of nupkgs on disk. All packages are cached during the first request in order
    /// to correctly determine attributes such as IsAbsoluteLatestVersion. Adding, removing, or making changes to packages on disk 
    /// will clear the cache.
    /// </summary>
    public class ServerPackageRepository : PackageRepositoryBase, IServerPackageRepository, IPackageLookup, IDisposable
    {
        private IDictionary<IPackage, DerivedPackageData> _packages;
        private readonly object _lockObj = new object();
        private readonly IFileSystem _fileSystem;
        private readonly IPackagePathResolver _pathResolver;
        private FileSystemWatcher _fileWatcher;
        private readonly string _filter = String.Format(CultureInfo.InvariantCulture, "*{0}", Constants.PackageExtension);
        private bool _monitoringFiles = false;

        public ServerPackageRepository(string path)
            : this(new DefaultPackagePathResolver(path), new PhysicalFileSystem(path))
        {

        }

        public ServerPackageRepository(IPackagePathResolver pathResolver, IFileSystem fileSystem)
        {
            if (pathResolver == null)
            {
                throw new ArgumentNullException("pathResolver");
            }

            if (fileSystem == null)
            {
                throw new ArgumentNullException("fileSystem");
            }

            _fileSystem = fileSystem;
            _pathResolver = pathResolver;
        }

        [Inject]
        public IHashProvider HashProvider { get; set; }

        public override IQueryable<IPackage> GetPackages()
        {
            return PackageCache.Keys.AsQueryable<IPackage>();
        }

        public IQueryable<Package> GetPackagesWithDerivedData()
        {
            var cache = PackageCache;
            return cache.Keys.Select(p => new Package(p, cache[p])).AsQueryable();
        }

        public bool Exists(string packageId, SemanticVersion version)
        {
            return FindPackage(packageId, version) != null;
        }

        public IPackage FindPackage(string packageId, SemanticVersion version)
        {
            return FindPackagesById(packageId).Where(p => p.Version.Equals(version)).FirstOrDefault();
        }

        public IEnumerable<IPackage> FindPackagesById(string packageId)
        {
            return GetPackages().Where(p => StringComparer.OrdinalIgnoreCase.Compare(p.Id, packageId) == 0);
        }

        /// <summary>
        /// Gives the Package containing both the IPackage and the derived metadata.
        /// The returned Package will be null if <paramref name="package" /> no longer exists in the cache.
        /// </summary>
        public Package GetMetadataPackage(IPackage package)
        {
            Package metadata = null;

            // The cache may have changed, and the metadata may no longer exist
            DerivedPackageData data = null;
            if (PackageCache.TryGetValue(package, out data))
            {
                metadata = new Package(package, data);
            }

            return metadata;
        }

        public IQueryable<IPackage> Search(string searchTerm, IEnumerable<string> targetFrameworks, bool allowPrereleaseVersions)
        {
            var cache = PackageCache;

            var packages = cache.Keys.AsQueryable().Find(searchTerm)
                                        .FilterByPrerelease(allowPrereleaseVersions)
                                        .Where(p => p.Listed)
                                        .AsQueryable();

            if (EnableFrameworkFiltering && targetFrameworks.Any())
            {
                // Get the list of framework names
                var frameworkNames = targetFrameworks.Select(frameworkName => VersionUtility.ParseFrameworkName(frameworkName));

                packages = packages.Where(package => frameworkNames.Any(frameworkName => VersionUtility.IsCompatible(frameworkName, cache[package].SupportedFrameworks)));
            }

            return packages;
        }

        public IEnumerable<IPackage> GetUpdates(IEnumerable<IPackageName> packages, bool includePrerelease, bool includeAllVersions, IEnumerable<FrameworkName> targetFrameworks, IEnumerable<IVersionSpec> versionConstraints)
        {
            return this.GetUpdatesCore(packages, includePrerelease, includeAllVersions, targetFrameworks, versionConstraints);
        }

        public override string Source
        {
            get
            {
                return _fileSystem.Root;
            }
        }

        public override bool SupportsPrereleasePackages
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// Add a file to the repository.
        /// </summary>
        public override void AddPackage(IPackage package)
        {
            string fileName = _pathResolver.GetPackageFileName(package);
            if (_fileSystem.FileExists(fileName) && !AllowOverrideExistingPackageOnPush)
            {
                throw new InvalidOperationException(String.Format(NuGetResources.Error_PackageAlreadyExists, package));
            }

            lock (_lockObj)
            {
                using (Stream stream = package.GetStream())
                {
                    _fileSystem.AddFile(fileName, stream);
                }

                InvalidatePackages();
            }
        }

        /// <summary>
        /// Unlist or delete a package
        /// </summary>
        public override void RemovePackage(IPackage package)
        {
            if (package != null)
            {
                string fileName = _pathResolver.GetPackageFileName(package);

                lock (_lockObj)
                {
                    if (EnableDelisting)
                    {
                        var fullPath = _fileSystem.GetFullPath(fileName);

                        if (File.Exists(fullPath))
                        {
                            File.SetAttributes(fullPath, File.GetAttributes(fullPath) | FileAttributes.Hidden);
                        }
                        else
                        {
                            Debug.Fail("unable to find file");
                        }
                    }
                    else
                    {
                        _fileSystem.DeleteFile(fileName);
                    }

                    InvalidatePackages();
                }
            }
        }

        /// <summary>
        /// Remove a package from the respository.
        /// </summary>
        public void RemovePackage(string packageId, SemanticVersion version)
        {
            IPackage package = FindPackage(packageId, version);

            RemovePackage(package);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            DetachEvents();
        }

        /// <summary>
        /// *.nupkg files in the root folder
        /// </summary>
        private IEnumerable<string> GetPackageFiles()
        {
            // Check top level directory
            foreach (var path in _fileSystem.GetFiles(String.Empty, _filter))
            {
                yield return path;
            }
        }

        /// <summary>
        /// Internal package cache containing both the packages and their metadata. 
        /// This data is generated if it does not exist already.
        /// </summary>
        private IDictionary<IPackage, DerivedPackageData> PackageCache
        {
            get
            {
                lock (_lockObj)
                {
                    if (_packages == null)
                    {
                        if (!_monitoringFiles)
                        {
                            // attach events the first time
                            _monitoringFiles = true;
                            AttachEvents();
                        }

                        _packages = CreateCache();
                    }

                    return _packages;
                }
            }
        }

        /// <summary>
        /// Sets the current cache to null so it will be regenerated next time.
        /// </summary>
        private void InvalidatePackages()
        {
            lock (_lockObj)
            {
                _packages = null;
            }
        }

        /// <summary>
        /// CreateCache loads all packages and determines additional metadata such as the hash, IsAbsoluteLatestVersion, and IsLatestVersion.
        /// </summary>
        private IDictionary<IPackage, DerivedPackageData> CreateCache()
        {
            ConcurrentDictionary<IPackage, DerivedPackageData> packages = new ConcurrentDictionary<IPackage, DerivedPackageData>();

            ParallelOptions opts = new ParallelOptions();
            opts.MaxDegreeOfParallelism = 4;

            ConcurrentDictionary<string, Tuple<IPackage, DerivedPackageData>> absoluteLatest = new ConcurrentDictionary<string, Tuple<IPackage, DerivedPackageData>>();
            ConcurrentDictionary<string, Tuple<IPackage, DerivedPackageData>> latest = new ConcurrentDictionary<string, Tuple<IPackage, DerivedPackageData>>();

            // get settings
            bool checkFrameworks = EnableFrameworkFiltering;
            bool enableDelisting = EnableDelisting;

            // load and cache all packages
            Parallel.ForEach(GetPackageFiles(), opts, path =>
            {
                OptimizedZipPackage zip = OpenPackage(path);

                Debug.Assert(zip != null, "Unable to open " + path);
                if (zip != null)
                {
                    if (enableDelisting)
                    {
                        // hidden packages are considered delisted
                        zip.Listed = !File.GetAttributes(_fileSystem.GetFullPath(path)).HasFlag(FileAttributes.Hidden);
                    }

                    byte[] hashBytes;
                    long fileLength;
                    using (Stream stream = _fileSystem.OpenFile(path))
                    {
                        fileLength = stream.Length;
                        hashBytes = HashProvider.CalculateHash(stream);
                    }

                    var data = new DerivedPackageData
                    {
                        PackageSize = fileLength,
                        PackageHash = Convert.ToBase64String(hashBytes),
                        LastUpdated = _fileSystem.GetLastModified(path),
                        Created = _fileSystem.GetCreated(path),
                        Path = path,
                        FullPath = _fileSystem.GetFullPath(path),

                        // default to false, these will be set later
                        IsAbsoluteLatestVersion = false,
                        IsLatestVersion = false
                    };

                    if (checkFrameworks)
                    {
                        data.SupportedFrameworks = zip.GetSupportedFrameworks();
                    }

                    Tuple<IPackage, DerivedPackageData> entry = new Tuple<IPackage, DerivedPackageData>(zip, data);

                    // find the latest versions
                    string id = zip.Id.ToLowerInvariant();

                    // update with the highest version
                    absoluteLatest.AddOrUpdate(id, entry, (oldId, oldEntry) => oldEntry.Item1.Version < entry.Item1.Version ? entry : oldEntry);

                    // update latest for release versions
                    if (zip.IsReleaseVersion())
                    {
                        latest.AddOrUpdate(id, entry, (oldId, oldEntry) => oldEntry.Item1.Version < entry.Item1.Version ? entry : oldEntry);
                    }

                    // add the package to the cache, it should not exist already
                    Debug.Assert(packages.ContainsKey(zip) == false, "duplicate package added");
                    packages.AddOrUpdate(zip, entry.Item2, (oldPkg, oldData) => oldData);
                }
            });

            // Set additional attributes after visiting all packages
            foreach (var entry in absoluteLatest.Values)
            {
                entry.Item2.IsAbsoluteLatestVersion = true;
            }

            foreach (var entry in latest.Values)
            {
                entry.Item2.IsLatestVersion = true;
            }

            return packages;
        }

        private OptimizedZipPackage OpenPackage(string path)
        {
            OptimizedZipPackage zip = null;

            if (_fileSystem.FileExists(path))
            {
                try
                {
                    zip = new OptimizedZipPackage(_fileSystem, path);
                }
                catch (FileFormatException ex)
                {
                    throw new InvalidDataException(String.Format(CultureInfo.CurrentCulture, NuGetResources.ErrorReadingPackage, path), ex);
                }
                // Set the last modified date on the package
                zip.Published = _fileSystem.GetLastModified(path);
            }

            return zip;
        }

        // Add the file watcher to monitor changes on disk
        private void AttachEvents()
        {
            // skip invalid paths
            if (_fileWatcher == null && !String.IsNullOrEmpty(Source) && Directory.Exists(Source))
            {
                _fileWatcher = new FileSystemWatcher(_fileSystem.Root);
                _fileWatcher.Filter = _filter;
                _fileWatcher.IncludeSubdirectories = false;

                _fileWatcher.Changed += FileChanged;
                _fileWatcher.Created += FileChanged;
                _fileWatcher.Deleted += FileChanged;
                _fileWatcher.Renamed += FileChanged;

                _fileWatcher.EnableRaisingEvents = true;
            }
        }

        // clean up events
        private void DetachEvents()
        {
            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.Changed -= FileChanged;
                _fileWatcher.Created -= FileChanged;
                _fileWatcher.Deleted -= FileChanged;
                _fileWatcher.Renamed -= FileChanged;
                _fileWatcher.Dispose();
                _fileWatcher = null;
            }
        }

        private void FileChanged(object sender, FileSystemEventArgs e)
        {
            // invalidate the cache when a nupkg in the root folder changes
            InvalidatePackages();
        }

        private static bool AllowOverrideExistingPackageOnPush
        {
            get
            {
                // If the setting is misconfigured, treat it as success (backwards compatibility).
                return GetBooleanAppSetting("allowOverrideExistingPackageOnPush", true);
            }
        }

        private static bool EnableDelisting
        {
            get
            {
                // If the setting is misconfigured, treat it as off (backwards compatibility).
                return GetBooleanAppSetting("enableDelisting", false);
            }
        }

        private static bool EnableFrameworkFiltering
        {
            get
            {
                // If the setting is misconfigured, treat it as off (backwards compatibility).
                return GetBooleanAppSetting("enableFrameworkFiltering", false);
            }
        }

        private static bool GetBooleanAppSetting(string key, bool defaultValue)
        {
            var appSettings = WebConfigurationManager.AppSettings;
            bool value;
            return !Boolean.TryParse(appSettings[key], out value) ? defaultValue : value;
        }
    }
}
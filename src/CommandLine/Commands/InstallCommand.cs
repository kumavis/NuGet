﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;

namespace NuGet.Commands
{
    [Command(typeof(NuGetCommand), "install", "InstallCommandDescription",
        MinArgs = 0, MaxArgs = 1, UsageSummaryResourceName = "InstallCommandUsageSummary",
        UsageDescriptionResourceName = "InstallCommandUsageDescription",
        UsageExampleResourceName = "InstallCommandUsageExamples")]
    public class InstallCommand : Command
    {
        private static readonly object _satelliteLock = new object();
        private readonly IPackageRepository _cacheRepository;
        private readonly List<string> _sources = new List<string>();

        private static readonly bool _isMonoRuntime = Type.GetType("Mono.Runtime") != null;
        
        [Option(typeof(NuGetCommand), "InstallCommandSourceDescription")]
        public ICollection<string> Source
        {
            get { return _sources; }
        }

        [Option(typeof(NuGetCommand), "InstallCommandOutputDirDescription")]
        public string OutputDirectory { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandVersionDescription")]
        public string Version { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandExcludeVersionDescription", AltName = "x")]
        public bool ExcludeVersion { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandPrerelease")]
        public bool Prerelease { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandNoCache")]
        public bool NoCache { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandRequireConsent")]
        public bool RequireConsent { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandSolutionDirectory")]
        public string SolutionDirectory { get; set; }

        [Option(typeof(NuGetCommand), "InstallCommandDisableParallel")]
        public bool DisableParallel { get; set; }

        /// <remarks>
        /// Meant for unit testing.
        /// </remarks>
        protected IPackageRepository CacheRepository
        {
            get { return _cacheRepository; }
        }

        private bool AllowMultipleVersions
        {
            get { return !ExcludeVersion; }
        }

        [ImportingConstructor]
        public InstallCommand()
            : this(MachineCache.Default)
        {
        }

        protected internal InstallCommand(
            IPackageRepository cacheRepository)
        {
            _cacheRepository = cacheRepository;

            // On mono, parallel builds are broken for some reason. See https://gist.github.com/4201936 for the errors
            // That are thrown.
            DisableParallel = _isMonoRuntime;
        }

        public override void ExecuteCommand()
        {
            string installPath = ResolveInstallPath();
            IFileSystem fileSystem = CreateFileSystem(installPath);

            // If the first argument is a packages.config file, install everything it lists
            // Otherwise, treat the first argument as a package Id
            if (Arguments.Count == 0 || Path.GetFileName(Arguments[0]).Equals(Constants.PackageReferenceFile, StringComparison.OrdinalIgnoreCase))
            {
                Prerelease = true;
                var configFilePath = Path.GetFullPath(Arguments.Count == 0 ? Constants.PackageReferenceFile : Arguments[0]);
                // By default the PackageReferenceFile does not throw if the file does not exist at the specified path.
                // We'll try reading from the file so that the file system throws a file not found
                EnsureFileExists(fileSystem, configFilePath);
                InstallPackagesFromConfigFile(fileSystem, GetPackageReferenceFile(configFilePath), configFilePath);
            }
            else
            {
                string packageId = Arguments[0];
                SemanticVersion version = Version != null ? new SemanticVersion(Version) : null;

                bool result = InstallPackage(fileSystem, packageId, version);
                if (!result)
                {
                    Console.WriteLine(NuGetResources.InstallCommandPackageAlreadyExists, packageId);
                }
            }
        }

        protected virtual PackageReferenceFile GetPackageReferenceFile(string path)
        {
            return new PackageReferenceFile(Path.GetFullPath(path));
        }

        internal string ResolveInstallPath()
        {
            if (!String.IsNullOrEmpty(OutputDirectory))
            {
                // Use the OutputDirectory if specified.
                return OutputDirectory;
            }

            // If the SolutionDir is specified, use the .nuget directory under it to determine the solution-level settings
            ISettings currentSettings = Settings;
            if (!String.IsNullOrEmpty(SolutionDirectory))
            {
                var solutionSettingsFile = Path.Combine(SolutionDirectory.TrimEnd(Path.DirectorySeparatorChar), NuGetConstants.NuGetSolutionSettingsFolder);
                var fileSystem = CreateFileSystem(solutionSettingsFile);

                currentSettings = NuGet.Settings.LoadDefaultSettings(
                    fileSystem, 
                    configFileName: null,
                    machineWideSettings: MachineWideSettings);

                // Recreate the source provider and credential provider
                SourceProvider = PackageSourceBuilder.CreateSourceProvider(currentSettings);
                HttpClient.DefaultCredentialProvider = new SettingsCredentialProvider(new ConsoleCredentialProvider(Console), SourceProvider, Console);
            }

            string installPath = currentSettings.GetRepositoryPath();
            if (!String.IsNullOrEmpty(installPath))
            {
                // If a value is specified in config, use that. 
                return installPath;
            }

            if (!String.IsNullOrEmpty(SolutionDirectory))
            {
                // For package restore scenarios, deduce the path of the packages directory from the solution directory.
                return Path.Combine(SolutionDirectory, CommandLineConstants.PackagesDirectoryName);
            }

            // Use the current directory as output.
            return Directory.GetCurrentDirectory();
        }

        private IPackageRepository GetRepository()
        {
            var repository = AggregateRepositoryHelper.CreateAggregateRepositoryFromSources(RepositoryFactory, SourceProvider, Source);
            bool ignoreFailingRepositories = repository.IgnoreFailingRepositories;
            if (!NoCache)
            {
                repository = new AggregateRepository(new[] { CacheRepository, repository }) { IgnoreFailingRepositories = ignoreFailingRepositories };
            }
            repository.Logger = Console;
            return repository;
        }

        private void InstallPackagesFromConfigFile(IFileSystem fileSystem, PackageReferenceFile file, string fileName)
        {
            var packageReferences = CommandLineUtility.GetPackageReferences(file, fileName, requireVersion: true);

            bool installedAny = ExecuteInParallel(fileSystem, packageReferences);
            if (!installedAny && packageReferences.Any())
            {
                Console.WriteLine(NuGetResources.InstallCommandNothingToInstall, Constants.PackageReferenceFile);
            }
        }

        /// <returns>True if one or more packages are installed.</returns>
        private bool ExecuteInParallel(IFileSystem fileSystem, ICollection<PackageReference> packageReferences)
        {
            bool packageRestoreConsent = new PackageRestoreConsent(Settings).IsGranted;
            int defaultConnectionLimit = ServicePointManager.DefaultConnectionLimit;
            if (packageReferences.Count > defaultConnectionLimit)
            {
                ServicePointManager.DefaultConnectionLimit = Math.Min(10, packageReferences.Count);
            }

            // The PackageSourceProvider reads from the underlying ISettings multiple times. One of the fields it reads is the password which is consequently decrypted
            // once for each package being installed. Per work item 2345, a couple of users are running into an issue where this results in an exception in native 
            // code. Instead, we'll use a cached set of sources. This should solve the issue and also give us some perf boost.
            SourceProvider = new CachedPackageSourceProvider(SourceProvider);

            var satellitePackages = new List<IPackage>();

            if (DisableParallel)
            {
                foreach(var package in packageReferences)
                {
                    RestorePackage(fileSystem, package.Id, package.Version, packageRestoreConsent, satellitePackages);
                }

                return true;
            }

            var tasks = packageReferences.Select(package =>
                            Task.Factory.StartNew(() => RestorePackage(fileSystem, package.Id, package.Version, packageRestoreConsent, satellitePackages))).ToArray();

            Task.WaitAll(tasks);
            // Return true if we installed any satellite packages or if any of our install tasks succeeded.
            return InstallSatellitePackages(fileSystem, satellitePackages) ||
                   tasks.All(p => !p.IsFaulted && p.Result);
        }

        private bool InstallSatellitePackages(IFileSystem fileSystem, List<IPackage> satellitePackages)
        {
            if (satellitePackages.Count == 0)
            {
                return false;
            }

            var packageManager = CreatePackageManager(fileSystem);
            foreach (var package in satellitePackages)
            {
                packageManager.InstallPackage(package, ignoreDependencies: true, allowPrereleaseVersions: Prerelease);
            }
            return true;
        }

        private bool RestorePackage(
            IFileSystem fileSystem,
            string packageId,
            SemanticVersion version,
            bool packageRestoreConsent,
            List<IPackage> satellitePackages)
        {
            var packageManager = CreatePackageManager(fileSystem);
            if (IsPackageInstalled(packageManager.LocalRepository, fileSystem, packageId, version))
            {
                return false;
            }

            EnsurePackageRestoreConsent(packageRestoreConsent);
            using (packageManager.SourceRepository.StartOperation(RepositoryOperationNames.Restore))
            {
                var package = PackageHelper.ResolvePackage(packageManager.SourceRepository, packageId, version);
                if (package.IsSatellitePackage())
                {
                    // Satellite packages would necessarily have to be installed later than the corresponding package. 
                    // We'll collect them in a list to keep track and then install them later.
                    lock (_satelliteLock)
                    {
                        satellitePackages.Add(package);
                    }
                    return true;
                }

                // During package restore with parallel build, multiple projects would try to write to disk simultaneously which results in write contentions.
                // We work around this issue by ensuring only one instance of the exe installs the package.
                PackageExtractor.InstallPackage(packageManager, package);
                return true;
            }
        }

        private bool InstallPackage(
            IFileSystem fileSystem,
            string packageId,
            SemanticVersion version)
        {
            var packageManager = CreatePackageManager(fileSystem);

            if (!AllowMultipleVersions)
            {
                var installedPackage = packageManager.LocalRepository.FindPackage(packageId);
                if (installedPackage != null)
                {
                    if (version != null && installedPackage.Version >= version)
                    {
                        // If the package is already installed (or the version being installed is lower), then we do not need to do anything. 
                        return false;
                    }
                    else if (packageManager.SourceRepository.Exists(packageId, version))
                    {
                        // If the package is already installed, but
                        // (a) the version we require is different from the one that is installed, 
                        // (b) side-by-side is disabled
                        // we need to uninstall it.
                        // However, before uninstalling, make sure the package exists in the source repository. 
                        packageManager.UninstallPackage(installedPackage, forceRemove: false, removeDependencies: true);
                    }
                }
            }

            using (packageManager.SourceRepository.StartOperation(RepositoryOperationNames.Install))
            {
                packageManager.InstallPackage(packageId, version, ignoreDependencies: false, allowPrereleaseVersions: Prerelease);
                return true;
            }
        }

        protected virtual IPackageManager CreatePackageManager(IFileSystem fileSystem)
        {
            var repository = GetRepository();
            var pathResolver = new DefaultPackagePathResolver(fileSystem, useSideBySidePaths: AllowMultipleVersions);

            IPackageRepository localRepository = new LocalPackageRepository(pathResolver, fileSystem);
            var packageManager = new PackageManager(repository, pathResolver, fileSystem, localRepository)
                                 {
                                     Logger = Console
                                 };

            return packageManager;
        }

        protected internal virtual IFileSystem CreateFileSystem(string path)
        {
            path = Path.GetFullPath(path);
            return new PhysicalFileSystem(path);
        }

        private static void EnsureFileExists(IFileSystem fileSystem, string configFilePath)
        {
            using (fileSystem.OpenFile(configFilePath))
            {
                // Do nothing
            }
        }

        private void EnsurePackageRestoreConsent(bool packageRestoreConsent)
        {
            if (RequireConsent && !packageRestoreConsent)
            {
                throw new InvalidOperationException(LocalizedResourceManager.GetString("InstallCommandPackageRestoreConsentNotFound"));
            }
        }

        // Do a very quick check of whether a package in installed by checking whether the nupkg file exists
        private bool IsPackageInstalled(IPackageRepository repository, IFileSystem fileSystem, string packageId, SemanticVersion version)
        {
            if (!AllowMultipleVersions)
            {
                // If we allow side-by-side, we'll check if any version of a package is installed. This operation is expensive since it involves
                // reading package metadata, consequently we don't use this approach when side-by-side isn't used.
                return repository.Exists(packageId);
            }
            else if (version != null)
            {
                // If we know exactly what package to lookup, check if it's already installed locally. 
                // We'll do this by checking if the package directory exists on disk.
                var localRepository = repository as LocalPackageRepository;
                Debug.Assert(localRepository != null, "The PackageManager's local repository instance is necessarily a LocalPackageRepository instance.");
                var packagePaths = localRepository.GetPackageLookupPaths(packageId, version);
                return packagePaths.Any(fileSystem.FileExists);
            }
            return false;
        }
    }
}
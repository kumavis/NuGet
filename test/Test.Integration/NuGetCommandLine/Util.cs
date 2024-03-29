﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Extensions;

namespace NuGet.Test.Integration.NuGetCommandLine
{
    public static class Util
    {
        /// <summary>
        /// Creates a test package.
        /// </summary>
        /// <param name="packageId">The id of the created package.</param>
        /// <param name="version">The version of the created package.</param>
        /// <param name="path">The directory where the package is created.</param>
        /// <returns>The full path of the created package file.</returns>
        public static string CreateTestPackage(string packageId, string version, string path, Uri licenseUrl = null)
        {
            var packageBuilder = new PackageBuilder
            {
                Id = packageId,
                Version = new SemanticVersion(version),
                Description = "Test desc"
            };

            if (licenseUrl != null)
            {
                packageBuilder.LicenseUrl = licenseUrl;
            }

            var dependencies = new PackageDependency("Dummy");
            packageBuilder.DependencySets.Add(new PackageDependencySet(null, new[] { dependencies }));
            packageBuilder.Authors.Add("test author");

            var packageFileName = string.Format("{0}.{1}.nupkg", packageId, version);
            var packageFileFullPath = Path.Combine(path, packageFileName);
            using (var fileStream = File.Create(packageFileFullPath))
            {
                packageBuilder.Save(fileStream);
            }

            return packageFileFullPath;
        }

        /// <summary>
        /// Creates the specified directory. If it exists, it's first deleted before 
        /// it's created. Thus, the directory is guaranteed to be empty.
        /// </summary>
        /// <param name="directory">The directory to be created.</param>
        public static void CreateDirectory(string directory)
        {
            Util.DeleteDirectory(directory);
            Directory.CreateDirectory(directory);
        }

        /// <summary>
        /// Deletes the specified directory.
        /// </summary>
        /// <param name="packageDirectory">The directory to be deleted.</param>
        public static void DeleteDirectory(string directory)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }

        /// <summary>
        /// Creates a file with the specified content.
        /// </summary>
        /// <param name="directory">The directory of the created file.</param>
        /// <param name="fileName">The name of the created file.</param>
        /// <param name="fileContent">The content of the created file.</param>
        public static void CreateFile(string directory, string fileName, string fileContent)
        {
            var fileFullName = Path.Combine(directory, fileName);
            using (var writer = new StreamWriter(fileFullName))
            {
                writer.Write(fileContent);
            }
        }
    }
}

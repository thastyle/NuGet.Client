// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.ProjectSystem.References;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.ProjectManagement.Projects;
using NuGet.ProjectModel;
using NuGet.Versioning;
using NuGet.VisualStudio;
using PackageReference = NuGet.Packaging.PackageReference;
using Task = System.Threading.Tasks.Task;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// Represents a project object associated with new VS "15" CPS project with package references.
    /// Key feature/difference is the project restore info is pushed by nomination API and stored in 
    /// a cache. Factory method retrieving the info from the cache should be provided.
    /// </summary>
    public class NetCorePackageReferenceProject : BuildIntegratedNuGetProject
    {
        private const string TargetFrameworkCondition = "TargetFramework";

        private readonly string _projectName;
        private readonly string _projectUniqueName;
        private readonly string _projectFullPath;

        private readonly IProjectSystemCache _projectSystemCache;
        private readonly UnconfiguredProject _unconfiguredProject;

        public NetCorePackageReferenceProject(
            string projectName,
            string projectUniqueName,
            string projectFullPath,
            IProjectSystemCache projectSystemCache,
            UnconfiguredProject unconfiguredProject,
            INuGetProjectServices projectServices,
            string projectId)
        {
            Assumes.Present(projectFullPath);
            Assumes.Present(projectSystemCache);
            Assumes.Present(projectServices);

            _projectName = projectName;
            _projectUniqueName = projectUniqueName;
            _projectFullPath = projectFullPath;

            ProjectStyle = ProjectStyle.PackageReference;

            _projectSystemCache = projectSystemCache;
            _unconfiguredProject = unconfiguredProject;
            ProjectServices = projectServices;

            InternalMetadata.Add(NuGetProjectMetadataKeys.Name, _projectName);
            InternalMetadata.Add(NuGetProjectMetadataKeys.UniqueName, _projectUniqueName);
            InternalMetadata.Add(NuGetProjectMetadataKeys.FullPath, _projectFullPath);
            InternalMetadata.Add(NuGetProjectMetadataKeys.ProjectId, projectId);
        }

        public override async Task<string> GetAssetsFilePathAsync()
        {
            return await GetAssetsFilePathAsync(shouldThrow: true);
        }

        public override async Task<string> GetAssetsFilePathOrNullAsync()
        {
            return await GetAssetsFilePathAsync(shouldThrow: false);
        }

        public override Task AddFileToProjectAsync(string filePath)
        {
            // sdk-style project system uses globbing to dynamically add files from project root into project
            // so we dont need to do anything explicitly here.
            return Task.CompletedTask;
        }

        private Task<string> GetAssetsFilePathAsync(bool shouldThrow)
        {
            var packageSpec = GetPackageSpec();
            if (packageSpec == null)
            {
                if (shouldThrow)
                {
                    throw new InvalidOperationException(
                        string.Format(Strings.ProjectNotLoaded_RestoreFailed, ProjectName));
                }
                else
                {
                    return Task.FromResult<string>(null);
                }
            }

            return Task.FromResult(GetAssetsFilePath(packageSpec.RestoreMetadata.OutputPath));
        }

        private static string GetAssetsFilePath(string outputPath)
        {
            return Path.Combine(
                outputPath,
                LockFileFormat.AssetsFileName);
        }

        private PackageSpec GetPackageSpec()
        {
            DependencyGraphSpec projectRestoreInfo;
            if (_projectSystemCache.TryGetProjectRestoreInfo(_projectFullPath, out projectRestoreInfo, out _))
            {
                return projectRestoreInfo.GetProjectSpec(_projectFullPath);
            }

            // if restore data was not found in the cache, meaning project nomination
            // didn't happen yet or failed.
            return null;
        }


        #region IDependencyGraphProject


        public override string MSBuildProjectPath => _projectFullPath;

        public override string ProjectName => _projectName;

        public override async Task<IReadOnlyList<PackageSpec>> GetPackageSpecsAsync(DependencyGraphCacheContext context)
        {
            var (dgSpecs, _) = await GetPackageSpecsAndAdditionalMessagesAsync(context);
            return dgSpecs;
        }

        public override Task<(IReadOnlyList<PackageSpec> dgSpecs, IReadOnlyList<IAssetsLogMessage> additionalMessages)> GetPackageSpecsAndAdditionalMessagesAsync(DependencyGraphCacheContext context)
        {
            var projects = new List<PackageSpec>();

            DependencyGraphSpec projectRestoreInfo;
            IReadOnlyList<IAssetsLogMessage> additionalMessages;
            if (!_projectSystemCache.TryGetProjectRestoreInfo(_projectFullPath, out projectRestoreInfo, out additionalMessages))
            {
                throw new InvalidOperationException(
                    string.Format(Strings.ProjectNotLoaded_RestoreFailed, ProjectName));
            }

            // Apply ISettings when needed to the return values.
            // This should not change the cached specs since they
            // contain values such as CLEAR which need to be persisted
            // and used here.
            var originalProjects = projectRestoreInfo.Projects;

            var settings = context?.Settings ?? NullSettings.Instance;

            foreach (var originalProject in originalProjects)
            {
                var project = originalProject.Clone();

                // Read restore settings from ISettings if it doesn't exist in the project
                // NOTE: Very important that the original project is used in the arguments, because cloning sorts the sources and compromises how the sources will be evaluated
                project.RestoreMetadata.PackagesPath = VSRestoreSettingsUtilities.GetPackagesPath(settings, originalProject);
                project.RestoreMetadata.Sources = VSRestoreSettingsUtilities.GetSources(settings, originalProject);
                project.RestoreMetadata.FallbackFolders = VSRestoreSettingsUtilities.GetFallbackFolders(settings, originalProject);
                project.RestoreMetadata.ConfigFilePaths = GetConfigFilePaths(settings);
                IgnoreUnsupportProjectReference(project);
                projects.Add(project);
            }

            if (context != null)
            {
                PackageSpec ignore;
                foreach (var project in projects
                    .Where(p => !context.PackageSpecCache.TryGetValue(
                        p.RestoreMetadata.ProjectUniqueName, out ignore)))
                {
                    context.PackageSpecCache.Add(
                        project.RestoreMetadata.ProjectUniqueName,
                        project);
                }
            }

            return Task.FromResult<(IReadOnlyList<PackageSpec>, IReadOnlyList<IAssetsLogMessage>)>((projects, additionalMessages));
        }

        private IList<string> GetConfigFilePaths(ISettings settings)
        {
            return settings.GetConfigFilePaths();
        }

        private void IgnoreUnsupportProjectReference(PackageSpec project)
        {
            foreach (var frameworkInfo in project.RestoreMetadata.TargetFrameworks)
            {
                var projectReferences = new List<ProjectRestoreReference>();

                foreach (var projectReference in frameworkInfo.ProjectReferences)
                {
                    if (SupportedProjectTypes.IsSupportedProjectExtension(projectReference.ProjectPath))
                    {
                        projectReferences.Add(projectReference);
                    }
                }

                frameworkInfo.ProjectReferences = projectReferences;
            }
        }

        #endregion

        #region NuGetProject

        public override Task<IEnumerable<PackageReference>> GetInstalledPackagesAsync(CancellationToken token)
        {
            PackageReference[] installedPackages;

            var packageSpec = GetPackageSpec();
            if (packageSpec != null)
            {
                installedPackages = GetPackageReferences(packageSpec);
            }
            else
            {
                installedPackages = new PackageReference[0];
            }

            return Task.FromResult<IEnumerable<PackageReference>>(installedPackages);
        }

        private static PackageReference[] GetPackageReferences(PackageSpec packageSpec)
        {
            var frameworkSorter = new NuGetFrameworkSorter();

            return packageSpec
                .TargetFrameworks
                .SelectMany(f => GetPackageReferences(f.Dependencies, f.FrameworkName))
                .GroupBy(p => p.PackageIdentity)
                .Select(g => g.OrderBy(p => p.TargetFramework, frameworkSorter).First())
                .ToArray();
        }

        private static IEnumerable<PackageReference> GetPackageReferences(IEnumerable<LibraryDependency> libraries, NuGetFramework targetFramework)
        {
            return libraries
                .Where(l => l.LibraryRange.TypeConstraint == LibraryDependencyTarget.Package)
                .Select(l => new BuildIntegratedPackageReference(l, targetFramework));
        }

        public override async Task<bool> InstallPackageAsync(
            string packageId,
            VersionRange range,
            INuGetProjectContext nuGetProjectContext,
            BuildIntegratedInstallationContext installationContext,
            CancellationToken token)
        {
            // Right now, the UI only handles installation of specific versions, which is just the minimum version of
            // the provided version range.
            var formattedRange = range.MinVersion.ToNormalizedString();

            nuGetProjectContext.Log(MessageLevel.Info, Strings.InstallingPackage, $"{packageId} {formattedRange}");

            if (installationContext.SuccessfulFrameworks.Any() && installationContext.UnsuccessfulFrameworks.Any())
            {
                // This is the "partial install" case. That is, install the package to only a subset of the frameworks
                // supported by this project.
                var conditionalService = _unconfiguredProject
                    .Services
                    .ExportProvider
                    .GetExportedValue<IConditionalPackageReferencesService>();

                if (conditionalService == null)
                {
                    throw new InvalidOperationException(string.Format(
                        Strings.UnableToGetCPSPackageInstallationService,
                        _projectFullPath));
                }

                foreach (var framework in installationContext.SuccessfulFrameworks)
                {
                    string originalFramework;
                    if (!installationContext.OriginalFrameworks.TryGetValue(framework, out originalFramework))
                    {
                        originalFramework = framework.GetShortFolderName();
                    }

                    var reference = await conditionalService.AddAsync(
                        packageId,
                        formattedRange,
                        TargetFrameworkCondition,
                        originalFramework);

                    // SuppressParent could be set to All if developmentDependency flag is true in package nuspec file.
                    if (installationContext.SuppressParent != LibraryIncludeFlagUtils.DefaultSuppressParent &&
                        installationContext.IncludeType != LibraryIncludeFlags.All)
                    {
                        await SetPackagePropertyValueAsync(
                            reference.Metadata,
                            ProjectItemProperties.PrivateAssets,
                            MSBuildStringUtility.Convert(LibraryIncludeFlagUtils.GetFlagString(installationContext.SuppressParent)));

                        await SetPackagePropertyValueAsync(
                            reference.Metadata,
                            ProjectItemProperties.IncludeAssets,
                            MSBuildStringUtility.Convert(LibraryIncludeFlagUtils.GetFlagString(installationContext.IncludeType)));
                    }
                }
            }
            else
            {
                // Install the package to all frameworks.
                var configuredProject = await _unconfiguredProject.GetSuggestedConfiguredProjectAsync();

                var result = await configuredProject
                    .Services
                    .PackageReferences
                    .AddAsync(packageId, formattedRange);

                // This is the update operation
                if (!result.Added)
                {
                    var existingReference = result.Reference;
                    await existingReference.Metadata.SetPropertyValueAsync("Version", formattedRange);
                }

                if (installationContext.SuppressParent != LibraryIncludeFlagUtils.DefaultSuppressParent &&
                    installationContext.IncludeType != LibraryIncludeFlags.All)
                {
                    await SetPackagePropertyValueAsync(
                        result.Reference.Metadata,
                        ProjectItemProperties.PrivateAssets,
                        MSBuildStringUtility.Convert(LibraryIncludeFlagUtils.GetFlagString(installationContext.SuppressParent)));

                    await SetPackagePropertyValueAsync(
                        result.Reference.Metadata,
                        ProjectItemProperties.IncludeAssets,
                        MSBuildStringUtility.Convert(LibraryIncludeFlagUtils.GetFlagString(installationContext.IncludeType)));
                }
            }

            return true;
        }

        private async Task SetPackagePropertyValueAsync(IProjectProperties metadata, string propertyName, string propertyValue)
        {
            await metadata.SetPropertyValueAsync(
                propertyName,
                propertyValue);
        }

        public override async Task<bool> UninstallPackageAsync(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            var configuredProject = await _unconfiguredProject.GetSuggestedConfiguredProjectAsync();
            await configuredProject?.Services.PackageReferences.RemoveAsync(packageIdentity.Id);
            return true;
        }

        public override Task<string> GetCacheFilePathAsync()
        {
            var spec = GetPackageSpec();
            if (spec == null)
            {
                throw new InvalidOperationException(
                    string.Format(Strings.ProjectNotLoaded_RestoreFailed, ProjectName));
            }

            return Task.FromResult(NoOpRestoreUtilities.GetProjectCacheFilePath(cacheRoot: spec.RestoreMetadata.OutputPath));
        }

        private WeakReference<DependencyGraphSpec> _lastUsedProjectRestoreInfo;
        private DateTime _lastAssetsFileWriteTime;
        private DateTime _lastTargetsFileWriteTime;
        private DateTime _lastPropsFileWriteTime;
        private DateTime _lastLockFileWriteTime;
        // if (PackagesLockFileUtilities.IsNuGetLockFileEnabled(request.Project))
        // var packageLockFilePath = PackagesLockFileUtilities.GetNuGetLockFilePath(request.Project);

        public override Task<bool> NeedsRestore()
        {
            // If the settings were updated. We need restore. => assume they haven't for now.
            bool IsUpToDate = true;

            // Check the last package spec.
            _projectSystemCache.TryGetProjectRestoreInfo(_projectFullPath, out DependencyGraphSpec projectRestoreInfo, out _);
            DependencyGraphSpec currentWeakReference = null;
            if (_lastUsedProjectRestoreInfo?.TryGetTarget(out currentWeakReference) ?? false)
            {
                if (currentWeakReference != null)
                {
                    IsUpToDate &= currentWeakReference.Equals(projectRestoreInfo);
                }
            }
            // Register the new dependency graph spec to be used.
            _lastUsedProjectRestoreInfo = new WeakReference<DependencyGraphSpec>(projectRestoreInfo);

            // Check the restore outputs on disk, ensure that they have not been changed since the last completed restore.
            var packageSpec = projectRestoreInfo.GetProjectSpec(_projectFullPath);
            GetOutputFilePaths(packageSpec, out string assetsFilePath, out string targetsFilePath, out string propsFilePath, out string lockFilePath);

            IsUpToDate &= AreOutputsUpToDate(assetsFilePath, targetsFilePath, propsFilePath, lockFilePath);

            // Always restore if the last status is a failure.
            if (!_lastRestoreStatus)
            {
                IsUpToDate = false;
            }

            return Task.FromResult(!IsUpToDate);
        }

        private static void GetOutputFilePaths(PackageSpec packageSpec, out string assetsFilePath, out string targetsFilePath, out string propsFilePath, out string lockFilePath)
        {
            assetsFilePath = GetAssetsFilePath(packageSpec.RestoreMetadata.OutputPath);
            targetsFilePath = BuildAssetsUtils.GetMSBuildFilePathForPackageReferenceStyleProject(packageSpec, BuildAssetsUtils.TargetsExtension);
            propsFilePath = BuildAssetsUtils.GetMSBuildFilePathForPackageReferenceStyleProject(packageSpec, BuildAssetsUtils.PropsExtension);
            lockFilePath = null; // fix the lock files later.
        }

        public override Task ReportRestoreStatusAsync(bool status)
        {
            _lastRestoreStatus = status;

            // If we can't get the project restore info, something must've changed, we just set the "last status to false in that case"
            if (_lastUsedProjectRestoreInfo.TryGetTarget(out DependencyGraphSpec dependencyGraphSpec))
            {
                var packageSpec = dependencyGraphSpec.GetProjectSpec(_projectFullPath);
                GetOutputFilePaths(packageSpec, out string assetsFilePath, out string targetsFilePath, out string propsFilePath, out string lockFilePath);

                _lastAssetsFileWriteTime = GetLastWriteTime(assetsFilePath);
                _lastTargetsFileWriteTime = GetLastWriteTime(targetsFilePath);
                _lastPropsFileWriteTime = GetLastWriteTime(propsFilePath);
                _lastLockFileWriteTime = GetLastWriteTime(lockFilePath);
            }
            else
            {
                _lastRestoreStatus = true;
            }

            return Task.CompletedTask;
        }

        private bool _lastRestoreStatus;

        private bool AreOutputsUpToDate(string assetsFilePath, string targetsFilePath, string propsFilePath, string lockFilePath)
        {
            DateTime currentAssetsFileWriteTime = GetLastWriteTime(assetsFilePath);
            DateTime currentTargetsFilePath = GetLastWriteTime(targetsFilePath);
            DateTime currentPropsFilePath = GetLastWriteTime(propsFilePath);
            DateTime currentLockFilePath = GetLastWriteTime(lockFilePath);

            return _lastAssetsFileWriteTime.Equals(currentAssetsFileWriteTime) &&
                   _lastTargetsFileWriteTime.Equals(currentTargetsFilePath) &&
                   _lastPropsFileWriteTime.Equals(currentPropsFilePath) &&
                   _lastLockFileWriteTime.Equals(currentLockFilePath);
        }

        private static DateTime GetLastWriteTime(string assetsFilePath)
        {
            if (!string.IsNullOrWhiteSpace(assetsFilePath))
            {
                var fileInfo = new FileInfo(assetsFilePath);
                if (fileInfo.Exists)
                {
                    return fileInfo.LastWriteTimeUtc;
                }
            }
            return default;
        }

        #endregion
    }
}

// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using NuGet.ProjectManagement;
using NuGet.ProjectModel;
using NuGet.VisualStudio;
using IAsyncServiceProvider = Microsoft.VisualStudio.Shell.IAsyncServiceProvider;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// Provides a method of creating <see cref="NetCorePackageReferenceProject"/> instance.
    /// </summary>
    [Export(typeof(INuGetProjectProvider))]
    [Name(nameof(NetCorePackageReferenceProjectProvider))]
    [Microsoft.VisualStudio.Utilities.Order(After = nameof(ProjectKNuGetProjectProvider))]
    public class NetCorePackageReferenceProjectProvider : INuGetProjectProvider
    {
        private static readonly string PackageReference = ProjectStyle.PackageReference.ToString();

        private readonly IProjectSystemCache _projectSystemCache;

        private readonly AsyncLazy<IComponentModel> _componentModel;

        public RuntimeTypeHandle ProjectType => typeof(NetCorePackageReferenceProject).TypeHandle;

        [ImportingConstructor]
        public NetCorePackageReferenceProjectProvider(IProjectSystemCache projectSystemCache)
            : this(AsyncServiceProvider.GlobalProvider, projectSystemCache)
        { }

        public NetCorePackageReferenceProjectProvider(
            IAsyncServiceProvider vsServiceProvider,
            IProjectSystemCache projectSystemCache)
        {
            Assumes.Present(vsServiceProvider);
            Assumes.Present(projectSystemCache);

            _projectSystemCache = projectSystemCache;

            _componentModel = new AsyncLazy<IComponentModel>(
                async () =>
                {
                    return await vsServiceProvider.GetServiceAsync<SComponentModel, IComponentModel>();
                },
                NuGetUIThreadHelper.JoinableTaskFactory);
        }

        public async Task<NuGetProject> TryCreateNuGetProjectAsync(
            IVsProjectAdapter vsProject, 
            ProjectProviderContext context, 
            bool forceProjectType)
        {
            Assumes.Present(vsProject);
            Assumes.Present(context);

            ThreadHelper.ThrowIfNotOnUIThread();

            // The project must be an IVsHierarchy.
            var hierarchy = vsProject.VsHierarchy;
            
            if (hierarchy == null)
            {
                return null;
            }

            // Check if the project is not CPS capable or if it is CPS capable then it does not have TargetFramework(s), if so then return false
            if (!hierarchy.IsCapabilityMatch("CPS"))
            {
                return null;
            }

            var buildProperties = vsProject.BuildProperties;

            // read MSBuild property RestoreProjectStyle, TargetFramework, and TargetFrameworks
            var restoreProjectStyle = await buildProperties.GetPropertyValueAsync(ProjectBuildProperties.RestoreProjectStyle);
            var targetFramework = await buildProperties.GetPropertyValueAsync(ProjectBuildProperties.TargetFramework);
            var targetFrameworks = await buildProperties.GetPropertyValueAsync(ProjectBuildProperties.TargetFrameworks);

            // check for RestoreProjectStyle property is set and if not set to PackageReference then return false
            if (!(string.IsNullOrEmpty(restoreProjectStyle) ||
                restoreProjectStyle.Equals(PackageReference, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
            // check whether TargetFramework or TargetFrameworks property is set, else return false
            else if (string.IsNullOrEmpty(targetFramework) && string.IsNullOrEmpty(targetFrameworks))
            {
                return null;
            }

            var fullProjectPath = vsProject.FullProjectPath;
            var unconfiguredProject = GetUnconfiguredProject(vsProject.Project);

            var projectServices = new NetCoreProjectSystemServices(vsProject, await _componentModel.GetValueAsync());

            return new NetCorePackageReferenceProject(
                vsProject.ProjectName,
                vsProject.CustomUniqueName,
                fullProjectPath,
                _projectSystemCache,
                unconfiguredProject,
                projectServices,
                vsProject.ProjectId);
        }

        private static UnconfiguredProject GetUnconfiguredProject(EnvDTE.Project project)
        {
            var context = project as IVsBrowseObjectContext;
            if (context == null)
            {
                // VC implements this on their DTE.Project.Object
                context = project.Object as IVsBrowseObjectContext;
            }
            return context?.UnconfiguredProject;
        }
    }
}

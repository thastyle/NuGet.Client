// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NuGet.Common;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Test.Utility;
using Xunit;

namespace NuGet.Protocol.Tests
{
    public class PackageSearchResourceV3Tests
    {
        private const string MinimalPackageJson = @"{""id"": ""NuGet.Versioning"",""version"": ""3.3.0""}";
        private static readonly PackageIdentity Identity = new PackageIdentity("NuGet.Versioning", NuGetVersion.Parse("3.3.0"));

        [Fact]
        public async Task PackageSearchResourceV3_IgnoresComments()
        {
            var responses = new Dictionary<string, string>();
            var response = $"{{\n// This is before\n\"data\":[{MinimalPackageJson}]\n// This is after\n}}";
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);
            responses.Add("https://api-v3search-0.nuget.org/query?q=&skip=0&take=1&prerelease=false&semVerLevel=2.0.0", response);

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);
            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act
            var packages = await resource.SearchAsync(
                string.Empty,
                new SearchFilter(false),
                skip: 0,
                take: 1,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None);

            // Assert
            var package = Assert.Single(packages);
            Assert.Equal(Identity, package.Identity);
        }

        [Fact]
        public async Task PackageSearchResourceV3_IgnoresPropertiesBeforeAndAfterData()
        {
            var responses = new Dictionary<string, string>();
            var response = $@"{{""noise"":true,""data"":[{MinimalPackageJson}],""interesting"":false}}";
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);
            responses.Add("https://api-v3search-0.nuget.org/query?q=&skip=0&take=1&prerelease=false&semVerLevel=2.0.0", response);

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);
            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act
            var packages = await resource.SearchAsync(
                string.Empty,
                new SearchFilter(false),
                skip: 0,
                take: 1,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None);

            // Assert
            var package = Assert.Single(packages);
            Assert.Equal(Identity, package.Identity);
        }

        [Theory]
        [MemberData(nameof(BadData))]
        public async Task PackageSearchResourceV3_IgnoresNonObjectsInHeterogeneousArrays(string data)
        {
            var responses = new Dictionary<string, string>();
            var response = $@"{{""data"":[{MinimalPackageJson},{data}]}}";
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);
            responses.Add("https://api-v3search-0.nuget.org/query?q=&skip=0&take=2&prerelease=false&semVerLevel=2.0.0", response);

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);
            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act
            var packages = await resource.SearchAsync(
                string.Empty,
                new SearchFilter(false),
                skip: 0,
                take: 2,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None);

            // Assert
            var package = Assert.Single(packages);
            Assert.Equal(Identity, package.Identity);
        }

        [Theory]
        [MemberData(nameof(BadData))]
        public async Task PackageSearchResourceV3_IgnoresArraysOfNonObjects(string data)
        {
            var responses = new Dictionary<string, string>();
            var response = $@"{{""data"":[{data}]}}";
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);
            responses.Add("https://api-v3search-0.nuget.org/query?q=&skip=0&take=1&prerelease=false&semVerLevel=2.0.0", response);

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);
            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act
            var packages = await resource.SearchAsync(
                string.Empty,
                new SearchFilter(false),
                skip: 0,
                take: 1,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None);

            // Assert
            Assert.Empty(packages);
        }

        [Theory]
        [MemberData(nameof(BadData))]
        [InlineData(@"{""id"": ""NuGet.Versioning""}")]
        public async Task PackageSearchResourceV3_ReturnsEmptyResultsForNonArrayData(string data)
        {
            var responses = new Dictionary<string, string>();
            var response = $@"{{""data"":{data}}}";
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);
            responses.Add("https://api-v3search-0.nuget.org/query?q=&skip=0&take=1&prerelease=false&semVerLevel=2.0.0", response);

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);
            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act
            var packages = await resource.SearchAsync(
                string.Empty,
                new SearchFilter(false),
                skip: 0,
                take: 1,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None);

            // Assert
            Assert.Empty(packages);
        }

        [Theory]
        [MemberData(nameof(BadDataAndType))]
        public async Task PackageSearchResourceV3_RejectsNonObjectResponses(string data, string type)
        {
            var responses = new Dictionary<string, string>();
            var searchUrl = "https://api-v3search-0.nuget.org/query?q=&skip=0&take=1&prerelease=false&semVerLevel=2.0.0";
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);
            responses.Add(searchUrl, data);

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);
            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<FatalProtocolException>(() => resource.SearchAsync(
                string.Empty,
                new SearchFilter(false),
                skip: 0,
                take: 1,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None));
            Assert.Equal($"Metadata could not be loaded from the source '{searchUrl}'.", ex.Message);
            var innerEx = Assert.IsType<JsonReaderException>(ex.InnerException);
            Assert.StartsWith($"Error reading JObject from JsonReader. Current JsonReader item is not an object: {type}.", innerEx.Message);
            Assert.Null(innerEx.InnerException);
        }

        [Fact]
        public async Task PackageSearchResourceV3_RejectsPartialObjects()
        {
            var responses = new Dictionary<string, string>();
            var searchUrl = "https://api-v3search-0.nuget.org/query?q=&skip=0&take=1&prerelease=false&semVerLevel=2.0.0";
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);
            responses.Add(searchUrl, $@"{{""data"":[{MinimalPackageJson},");

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);
            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<FatalProtocolException>(() => resource.SearchAsync(
                string.Empty,
                new SearchFilter(false),
                skip: 0,
                take: 1,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None));
            Assert.Equal($"Metadata could not be loaded from the source '{searchUrl}'.", ex.Message);
            var innerEx = Assert.IsType<JsonReaderException>(ex.InnerException);
            Assert.StartsWith($"Unexpected end of content while loading JObject. Path 'data[0]',", innerEx.Message);
            Assert.Null(innerEx.InnerException);
        }

        [Fact]
        public async Task PackageSearchResourceV3_RejectsUnexpectedEndOfContent()
        {
            var responses = new Dictionary<string, string>();
            var searchUrl = "https://api-v3search-0.nuget.org/query?q=&skip=0&take=1&prerelease=false&semVerLevel=2.0.0";
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);
            responses.Add(searchUrl, $@"{{""data"":[{MinimalPackageJson}],");

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);
            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<FatalProtocolException>(() => resource.SearchAsync(
                string.Empty,
                new SearchFilter(false),
                skip: 0,
                take: 1,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None));
            Assert.Equal($"Metadata could not be loaded from the source '{searchUrl}'.", ex.Message);
            var innerEx = Assert.IsType<JsonReaderException>(ex.InnerException);
            Assert.StartsWith($"Unexpected end of content while loading JObject.", innerEx.Message);
            Assert.Null(innerEx.InnerException);
        }

        public static IEnumerable<object[]> BadDataAndType => new object[][]
        {
            new object[] { @"""NuGet.Frameworks""", "String" },
            new object[] { "42", "Integer" },
            new object[] { "13.37", "Float" },
            new object[] { "true", "Boolean" },
            new object[] { "false", "Boolean" },
            new object[] { "null", "Null" },
        };

        public static IEnumerable<object[]> BadData => BadDataAndType.Select(x => new[] { x[0] });

        [Fact]
        public async Task PackageSearchResourceV3_GetMetadataAsync()
        {
            // Arrange
            var responses = new Dictionary<string, string>();
            responses.Add("https://api-v3search-0.nuget.org/query?q=entityframework&skip=0&take=1&prerelease=false&semVerLevel=2.0.0",
                ProtocolUtility.GetResource("NuGet.Protocol.Tests.compiler.resources.EntityFrameworkSearch.json", GetType()));
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);

            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act
            var packages = await resource.SearchAsync(
                "entityframework",
                new SearchFilter(false),
                skip: 0,
                take: 1,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None);

            var package = packages.SingleOrDefault();

            // Assert
            Assert.NotNull(package);
            Assert.Equal("Microsoft", package.Authors);
            Assert.Equal("Entity Framework is Microsoft's recommended data access technology for new applications.", package.Description);
            Assert.Equal(package.Description, package.Summary);
            Assert.Equal("EntityFramework", package.Title);
            Assert.Equal(string.Join(", ", "Microsoft", "EF", "Database", "Data", "O/RM", "ADO.NET"), package.Tags);
            Assert.True(package.PrefixReserved);
        }

        [Fact]
        public async Task PackageSearchResourceV3_UsesReferenceCache()
        {
            // Arrange
            var responses = new Dictionary<string, string>();
            responses.Add("https://api-v3search-0.nuget.org/query?q=entityframework&skip=0&take=1&prerelease=false&semVerLevel=2.0.0",
                ProtocolUtility.GetResource("NuGet.Protocol.Tests.compiler.resources.SearchV3WithDuplicateBesidesVersion.json", GetType()));
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);

            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act
            var packages = (IEnumerable<PackageSearchMetadataBuilder.ClonedPackageSearchMetadata>)await resource.SearchAsync(
                "entityframework",
                new SearchFilter(false),
                skip: 0,
                take: 1,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None);

            var first = packages.ElementAt(0);
            var second = packages.ElementAt(1);

            // Assert
            MetadataReferenceCacheTestUtility.AssertPackagesHaveSameReferences(first, second);
        }

        [Fact]
        public async Task PackageSearchResourceV3_GetMetadataAsync_NotFound()
        {
            // Arrange
            var responses = new Dictionary<string, string>();
            responses.Add("https://api-v3search-0.nuget.org/query?q=yabbadabbadoo&skip=0&take=1&prerelease=false&semVerLevel=2.0.0",
                ProtocolUtility.GetResource("NuGet.Protocol.Tests.compiler.resources.EmptySearchResponse.json", GetType()));
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);

            var resource = await repo.GetResourceAsync<PackageSearchResource>();

            // Act
            var packages = await resource.SearchAsync(
                "yabbadabbadoo",
                new SearchFilter(false),
                skip: 0,
                take: 1,
                log: NullLogger.Instance,
                cancellationToken: CancellationToken.None);

            // Assert
            Assert.Empty(packages);
        }
    }
}

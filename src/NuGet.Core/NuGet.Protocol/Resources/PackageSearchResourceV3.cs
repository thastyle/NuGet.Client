// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NuGet.Protocol.Core.Types;

namespace NuGet.Protocol
{
    public class PackageSearchResourceV3 : PackageSearchResource
    {
        private readonly RawSearchResourceV3 _rawSearchResource;

        public PackageSearchResourceV3(RawSearchResourceV3 searchResource)
            : base()
        {
            _rawSearchResource = searchResource;
        }

        public override async Task<IEnumerable<IPackageSearchMetadata>> SearchAsync(string searchTerm, SearchFilter filter, int skip, int take, Common.ILogger log, CancellationToken cancellationToken)
        {
            var searchResultMetadata = await _rawSearchResource.Search(
                (httpSource, uri) => httpSource.ProcessStreamAsync(
                    new HttpSourceRequest(uri, Common.NullLogger.Instance),
                    s => ProgressStreamAsync(s, cancellationToken),
                    log,
                    cancellationToken),
                searchTerm,
                filter,
                skip,
                take);

            var metadataCache = new MetadataReferenceCache();

            var searchResults = searchResultMetadata
                .Select(m => m.WithVersions(() => GetVersions(m, filter)))
                .Select(m => metadataCache.GetObject((PackageSearchMetadataBuilder.ClonedPackageSearchMetadata) m))
                .ToArray();

            return searchResults;
        }

        private static async Task<IEnumerable<PackageSearchMetadata>> ProgressStreamAsync(
            Stream stream,
            CancellationToken token)
        {
            // TODO: test
            // - what if the JSON is not an object at the root
            // - what if "data" is an object? or a scalar?

            var output = Enumerable.Empty<PackageSearchMetadata>();
            using (var seekableStream = await stream.AsSeekableStreamAsync(token))
            using (var streamReader = new StreamReader(seekableStream))
            using (var jsonTextReader = new JsonTextReader(streamReader))
            {
                Assert(jsonTextReader.Read(), "No JSON found.");
                Assert(jsonTextReader.TokenType == JsonToken.StartObject, "Expected a JSON object.");
                Assert(jsonTextReader.Read(), "Expected JSON after the start object");

                var dataFound = false;
                while (!dataFound && jsonTextReader.TokenType != JsonToken.EndObject)
                {
                    Assert(jsonTextReader.TokenType == JsonToken.PropertyName, "No property name found.");
                    dataFound = "data".Equals(jsonTextReader.Value);
                    if (dataFound)
                    {
                        Assert(jsonTextReader.Read(), "No JSON found.");
                        if (jsonTextReader.TokenType != JsonToken.StartArray)
                        {
                            break;
                        }
                        output = JsonExtensions.JsonObjectSerializer.Deserialize<List<PackageSearchMetadata>>(jsonTextReader);
                        Assert(jsonTextReader.TokenType == JsonToken.EndArray, "Expected end of JSON array.");
                    }
                    else
                    {
                        jsonTextReader.Skip();
                        Assert(jsonTextReader.Read(), "No JSON found.");
                    }
                }

                return output;
            }
        }

        private static void Assert(bool value, string message)
        {
            if (!value)
            {
                throw new JsonSerializationException(message);
            }
        }

        private static IEnumerable<VersionInfo> GetVersions(PackageSearchMetadata metadata, SearchFilter filter)
        {
            var versions = metadata.ParsedVersions;

            // TODO: in v2, we only have download count for all versions, not per version.
            // To be consistent, in v3, we also use total download count for now.
            var totalDownloadCount = versions.Select(v => v.DownloadCount).Sum();
            versions = versions
                .Select(v => v.Version)
                .Where(v => filter.IncludePrerelease || !v.IsPrerelease)
                .Concat(new[] { metadata.Version })
                .Distinct()
                .Select(v => new VersionInfo(v, totalDownloadCount))
                .ToArray();

            return versions;
        }
    }
}

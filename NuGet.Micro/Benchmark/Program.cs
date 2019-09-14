using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using NuGet.Protocol.Core.Types;
using Test.Utility;

namespace Benchmark
{
    [MemoryDiagnoser]
    public class Search
    {
        public static readonly string ResponseDirectory = "SearchResults";

        [ParamsSource(nameof(TakeValues))]
        public int Take;

        private PackageSearchResource _resource;

        [GlobalSetup]
        public async Task SetupAsync()
        {
            var responses = new Dictionary<string, string>();
            responses.Add("http://testsource.com/v3/index.json", JsonData.IndexWithoutFlatContainer);
            var response = File.ReadAllText(Path.Combine(ResponseDirectory, $"{Take:D4}.json"));
            responses.Add($"https://api-v3search-0.nuget.org/query?q=&skip=0&take={Take}&prerelease=false&semVerLevel=2.0.0", response);

            var repo = StaticHttpHandler.CreateSource("http://testsource.com/v3/index.json", Repository.Provider.GetCoreV3(), responses);
            _resource = await repo.GetResourceAsync<PackageSearchResource>();
        }

        public IEnumerable<int> TakeValues
        {
            get
            {
                foreach (var file in Directory.EnumerateFiles(ResponseDirectory, "*.json", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    yield return int.Parse(fileName);
                }
            }
        }

        [Benchmark]
        public async Task TestAsync()
        {
            await _resource.SearchAsync(
                string.Empty,
                new SearchFilter(false),
                skip: 0,
                take: Take,
                log: NuGet.Common.NullLogger.Instance,
                cancellationToken: CancellationToken.None);
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            MainAsync().GetAwaiter().GetResult();
        }

        static async Task MainAsync()
        {
            Console.WriteLine("Ensuring all search result data exists...");
            using (var httpClient = new HttpClient())
            {
                int count = 0;
                while (true)
                {
                    var take = count * 100;
                    var filePath = Path.Combine(Search.ResponseDirectory, $"{take:D4}.json");
                    Directory.CreateDirectory("SearchResults");

                    if (!File.Exists(filePath))
                    {
                        var url = $"https://azuresearch-usnc.nuget.org/query?q=&skip=0&take={take}&prerelease=true&semVerLevel=2.0.0";
                        Console.WriteLine(url);
                        using (var responseStream = await httpClient.GetStreamAsync(url))
                        using (var responseFileStream = new FileStream(filePath, FileMode.Create))
                        {
                            await responseStream.CopyToAsync(responseFileStream);
                        }
                    }

                    count++;

                    if (count == 11)
                    {
                        break;
                    }
                }
            }

            Console.WriteLine("Running benchmark...");
            var summary = BenchmarkRunner.Run<Search>();
        }
    }
}

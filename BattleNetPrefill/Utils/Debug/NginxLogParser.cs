﻿using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using BattleNetPrefill.Extensions;
using BattleNetPrefill.Structs;
using BattleNetPrefill.Utils.Debug.Models;
using Spectre.Console;
using Utf8Json;
using Utf8Json.Resolvers;
using static BattleNetPrefill.Utils.SpectreColors;

namespace BattleNetPrefill.Utils.Debug
{
    public static class NginxLogParser
    {
        private static IJsonFormatterResolver DefaultUtf8JsonResolver = CompositeResolver.Create(new IJsonFormatter[] { new RootFolderFormatter() }, 
                                                                                                    new[] { StandardResolver.Default });

        /// <summary>
        /// Gets the most recently saved request logs for the specified product.  The request logs will be generated by installing a product (ex. Starcraft)
        /// using the Battle.Net client, and then saving off the logged requests from Nginx.  These request files can then be compared against, to verify if
        /// our application is making the same requests as Battle.Net, without actually having to make the requests (saves bandwidth and time)
        /// </summary>
        /// <param name="logBasePath">Root folder where all log files are stored.</param>
        /// <param name="product">Target product to be parsed.  Used to determine subfolder to search for files</param>
        /// <returns>A list of all requests made by the real Battle.Net client.</returns>
        public static List<Request> GetSavedRequestLogs(string logBasePath, TactProduct product)
        {
            var timer = Stopwatch.StartNew();
            var logFolder = $@"{logBasePath}/{product.DisplayName.Replace(":", "")}";

            var latestFile = new DirectoryInfo(logFolder)
                                    .GetFiles()
                                    .OrderByDescending(e => e.LastWriteTime)
                                    .FirstOrDefault();
            
            // Loading the pre-computed log file if it exists, speeds up subsequent runs
            if (latestFile.FullName.Contains("coalesced"))
            {
                return JsonSerializer.Deserialize<List<Request>>(File.ReadAllText(latestFile.FullName), DefaultUtf8JsonResolver);
            }
            if (latestFile.Extension == ".zip")
            {
                // Extract the logs, so that we can read them while debugging
                ZipFile.ExtractToDirectory(latestFile.FullName, latestFile.Directory.FullName, true);
                var logFilePath = latestFile.FullName.Replace(".zip", ".log");

                var rawLogs = ParseRequestLogs(File.ReadAllLines(logFilePath));
                List<Request> requestsToReplay = RequestUtils.CoalesceRequests(rawLogs);

                // Save the coalesced results to speed up future runs
                var coalescedFileName = $@"{logFolder}/{latestFile.Name.Replace(".zip", ".coalesced.log")}";
                File.WriteAllText(coalescedFileName, JsonSerializer.ToJsonString(requestsToReplay, DefaultUtf8JsonResolver));
                
                AnsiConsole.Console.MarkupLineTimer("Parsed request logs", timer);
                return requestsToReplay;
            }

            throw new FileNotFoundException($"Unable to find replay logs for {product.DisplayName}");
        }

        private static List<Request> ParseRequestLogs(string[] rawRequests)
        {
            var parsedRequests = new List<Request>();

            foreach (var rawRequest in rawRequests)
            {
                // Only interested in GET requests from Battle.Net.  Filtering out any other requests from other clients like Steam
                if (!(rawRequest.Contains("GET") && rawRequest.Contains("[blizzard]")))
                {
                    continue;
                }
                if (rawRequest.Contains("bnt002") || rawRequest.Contains("bnt004"))
                {
                    continue;
                }
                if (rawRequest.Contains("tpr/catalogs"))
                {
                    continue;
                }

                // Find all matches between double quotes.  This will be the only info that we care about in the request logs.
                // Uri example : /tpr/sc1live/data/b5/20/b520b25e5d4b5627025aeba235d60708.
                var requestUrlMatches = Regex.Matches(rawRequest, @"(tpr/.*/\w*/[a-z0-9]{2}/[a-z0-9]{2}/[a-z0-9]+)(.index)?");
                var requestUrl = requestUrlMatches[0].Value;

                var requestSplit = requestUrl.Split("/");

                var parsedRequest = new Request
                {
                    ProductRootUri = $"tpr/{requestSplit[1]}",
                    RootFolder = RootFolder.Parse(requestSplit[2]),
                    CdnKey = requestSplit[5].Replace(".index", "").ToMD5(),

                    IsIndex = requestUrl.Contains(".index")
                };

                // Pulling out byte ranges ex. "0-4095" from logs
                var byteMatch = Regex.Match(rawRequest, @"bytes=(\d+-\d+)");
                if (byteMatch.Success)
                {
                    var bytesSplit = byteMatch.Groups[1].Value.Split("-");
                    parsedRequest.LowerByteRange = long.Parse(bytesSplit[0]);
                    parsedRequest.UpperByteRange = long.Parse(bytesSplit[1]);
                }
                else
                {
                    parsedRequest.DownloadWholeFile = true;
                }

                parsedRequests.Add(parsedRequest);
            }

            return parsedRequests;
        }
        
        public static string GetLatestLogVersionForProduct(string logBasePath, TactProduct product)
        {
            var logFolder = $@"{logBasePath}\{product.DisplayName.Replace(":", "")}";

            if (!Directory.Exists(logFolder))
            {
                Directory.CreateDirectory(logFolder);
            }

            var latestFile = new DirectoryInfo(logFolder)
                .GetFiles()
                .Where(e => e.Extension == ".zip")
                .OrderByDescending(e => e.LastWriteTime)
                .FirstOrDefault();

            if (latestFile == null)
            {
                return "";
            }

            return latestFile.Name.Replace(".zip", "");
        }
    }
}
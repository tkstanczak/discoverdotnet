﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AngleSharp;
using DiscoverDotnet.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Octokit;
using Statiq.Common;

namespace DiscoverDotnet.Modules
{
    // Gets project GitHub and NuGet data
    public class GetProjectData : Module
    {
        private static readonly IConfiguration AngleSharpConfig = Configuration.Default.WithDefaultLoader();

        private GitHubManager _gitHub;
        private FoundationManager _foundation;

        protected override async Task BeforeExecutionAsync(IExecutionContext context)
        {
            if (_gitHub == null)
            {
                _gitHub = context.GetRequiredService<GitHubManager>();
            }
            if (_foundation == null)
            {
                _foundation = context.GetRequiredService<FoundationManager>();
                await _foundation.PopulateAsync(context);
            }
        }

        protected override async Task<IEnumerable<IDocument>> ExecuteInputAsync(IDocument input, IExecutionContext context)
        {
            ConcurrentDictionary<string, object> metadata = new ConcurrentDictionary<string, object>();
            await GetProjectGitHubDataAsync(input, context, metadata);
            await GetProjectNuGetDataAsync(input, context, metadata);
            return metadata.Count > 0 ? input.Clone(metadata).Yield() : input.Yield();
        }

        private async Task GetProjectGitHubDataAsync(IDocument input, IExecutionContext context, ConcurrentDictionary<string, object> metadata)
        {
            // Extract the GitHub owner and name
            if (Uri.TryCreate(input.GetString("SourceCode"), UriKind.Absolute, out Uri sourceCode)
                && sourceCode.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
            {
                string owner = sourceCode.Segments[1].Trim('/');
                string name = sourceCode.Segments[2].Trim('/');

                // Get the repository
                context.LogInformation($"Getting GitHub project data for {owner}/{name}");
                Repository repository = await _gitHub.GetAsync(x => x.Repository.Get(owner, name), context);

                // Get the metadata
                metadata.TryAdd("StargazersCount", repository.StargazersCount);
                metadata.TryAdd("ForksCount", repository.ForksCount);
                metadata.TryAdd("OpenIssuesCount", repository.OpenIssuesCount);
                metadata.TryAdd("PushedAt", repository.PushedAt);
                metadata.TryAdd("CreatedAt", repository.CreatedAt);
                metadata.TryAdd("Title", repository.Name);
                metadata.TryAdd("Description", repository.Description);
                metadata.TryAdd("Website", repository.Homepage);
                if (GitHubManager.MicrosoftOwners.Contains(owner))
                {
                    metadata.GetOrAdd("Microsoft", true);
                }
                if (_foundation.IsInFoundation(owner, name))
                {
                    metadata.GetOrAdd("Foundation", true);
                }

                // Get the readme (will throw if there's no readme)
                context.LogInformation($"Getting GitHub readme for {owner}/{name}");
                try
                {
                    string readme = await _gitHub.GetAsync(x => x.Repository.Content.GetReadmeHtml(owner, name), context, false);
                    if (!string.IsNullOrEmpty(readme))
                    {
                        metadata.TryAdd("Readme", readme);
                    }
                }
                catch (Exception ex)
                {
                    context.LogInformation($"Could not get GitHub readme for {owner}/{name}: {ex.Message}");
                }
            }
        }

        private async Task GetProjectNuGetDataAsync(IDocument input, IExecutionContext context, ConcurrentDictionary<string, object> metadata)
        {
            List<Package> packageData = new List<Package>();
            IReadOnlyList<string> packages = input.GetList("NuGet", Array.Empty<string>());
            foreach (string package in packages.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                context.LogInformation($"Getting NuGet data for {package}");
                try
                {
                    IBrowsingContext browsingContext = BrowsingContext.New(AngleSharpConfig);
                    AngleSharp.Dom.IDocument document = await browsingContext.OpenAsync($"https://www.nuget.org/packages/{package}");
                    if (document.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        context.LogWarning($"Bad status code for {package}: {document.StatusCode}");
                    }
                    else if (document == null)
                    {
                        context.LogWarning($"Could not get document for {package}");
                    }
                    else
                    {
                        Package data = new Package
                        {
                            Id = package
                        };

                        // Get statistics
                        AngleSharp.Dom.IElement statistics = document
                            .QuerySelectorAll(".package-details-info h2")
                            .First(x => x.TextContent == "Statistics")
                            .NextElementSibling;
                        data.TotalDownloads = statistics.Children
                            .First(x => x.TextContent.Contains("total downloads"))
                            .TextContent.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                        data.PerDayDownloads = statistics.Children
                            .First(x => x.TextContent.Contains("per day"))
                            .TextContent.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];

                        // Get versions
                        data.Versions = document
                            .QuerySelectorAll("#version-history table tbody tr")
                            .Select(x => new PackageVersion(x))
                            .ToList();

                        // Add the data
                        packageData.Add(data);
                    }
                }
                catch (Exception ex)
                {
                    context.LogWarning($"Error getting NuGet data for {package}: {ex.Message}");
                }
            }

            if (packageData.Count > 0)
            {
                metadata.TryAdd("NuGetPackages", packageData);
            }
        }
    }
}

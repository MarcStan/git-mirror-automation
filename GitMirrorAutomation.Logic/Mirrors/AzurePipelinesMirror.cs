using GitMirrorAutomation.Logic.Config;
using GitMirrorAutomation.Logic.Helpers;
using GitMirrorAutomation.Logic.Models;
using GitMirrorAutomation.Logic.Sources;
using GitMirrorAutomation.Logic.Targets;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitMirrorAutomation.Logic.Mirrors
{
    public class AzurePipelinesMirror : AzureDevOpsBase, IMirrorService
    {
        private readonly MirrorViaConfig _config;
        private int? _buildToCloneId;
        private readonly IRepositorySource _repositorySource;
        private readonly ILogger _log;

        public AzurePipelinesMirror(
            MirrorViaConfig config,
            IRepositorySource repositorySource,
            ILogger log)
            : base(config.Type, config.AccessToken)
        {
            _config = config;
            _repositorySource = repositorySource;
            _log = log;

            if (DevOpsProject == null)
                throw new NotSupportedException("Mirror must be set up in a specific project and does not support organization level!");
        }

        public string MirrorId => $"dev.azure.com/{DevOpsOrganization}/{DevOpsProject}";

        public async Task<Mirror[]> GetExistingMirrorsAsync(CancellationToken cancellationToken)
        {
            await EnsureAccessToken(cancellationToken);

            var mirrors = new List<Mirror>();
            var builds = await GetBuildsAsync(cancellationToken);
            var mirrorBuilds = builds.Where(b => b.Name.StartsWith(_config.BuildNamePrefix)).ToArray();
            _log.LogInformation($"Looking for existing mirrors in {builds.Length} builds ({mirrorBuilds.Length} of those are mirror builds)..");
            foreach (var build in mirrorBuilds)
            {
                if (!_buildToCloneId.HasValue &&
                    _config.BuildToClone.Equals(build.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _buildToCloneId = build.Id;
                }

                var repoName = build.Name.Substring(_config.BuildNamePrefix.Length);
                var repo = new Repository
                {
                    Name = repoName
                };
                if (_repositorySource.Type == "dev.azure.com" &&
                    repoName.Contains(" - "))
                {
                    var ado = (AzureDevOpsRepositoryTarget)_repositorySource;
                    // ADO to ADO clone uses "Project - Repo" in build name when repo is in different repo than the build
                    var parts = repoName.Split(" - ");
                    repo = new AzureDevOpsRepository
                    {
                        Name = parts[1],
                        GitUrl = $"https://dev.azure.com/{ado.DevOpsOrganization}/{parts[0]}/_git/{parts[1]}"
                    };
                }
                var expectedRepoUrls = _repositorySource.GetRepositoryUrls(repo);

                // repo property isn't loaded in overall list, so get definition
                // to ensure build is using correct source repo
                var buildDefinition = await GetBuildDefinitionAsync(build.Id, cancellationToken);
                var buildWithRepo = JsonSerializer.Deserialize<Build>(buildDefinition.GetRawText(), JsonSettings.Default);
                // normalize because github/gitlab allow both urls ending in .git and without
                // and Azure DevOps builds can also use both types of urls as source for builds
                var foundUrl = Normalize(buildWithRepo!.Repository.Url);
                expectedRepoUrls = expectedRepoUrls.Select(Normalize).ToArray();
                if (!expectedRepoUrls.Contains(foundUrl))
                    throw new NotSupportedException($"Expected build '{build.Name}' to use one of these repos: '{string.Join(", ", expectedRepoUrls)}' as source but it uses '{buildWithRepo.Repository.Url}'");

                mirrors.Add(new Mirror
                {
                    BuildName = build.Name,
                    Id = build.Id,
                    Repository = repo.Name
                });
            }
            if (!_buildToCloneId.HasValue)
                throw new InvalidOperationException($"Could not find the build to clone '{_config.BuildToClone}' while scanning builds in Azure DevOps");

            return mirrors.ToArray();
        }

        private string Normalize(string url)
        {
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                return url.Substring(0, url.Length - ".git".Length);
            return url;
        }

        public async Task SetupMirrorAsync(IRepository repository, CancellationToken cancellationToken)
        {
            if (!_buildToCloneId.HasValue)
                throw new InvalidOperationException($"Must call {nameof(GetExistingMirrorsAsync)} first before setting up a new mirror!");

            await EnsureAccessToken(cancellationToken);

            var buildDefinition = await GetBuildDefinitionAsync(_buildToCloneId.Value, cancellationToken);
            // real inefficient but there seems to be no way to modify a JsonElement + this usually isn't executed a million times..
            var jObject = JObject.Parse(buildDefinition.GetRawText());

            // repository settings depend on the type of source
            switch (_repositorySource.Type)
            {
                case "github.com":
                    var gh = (GithubRepositorySource)_repositorySource;
                    var githubWebUrl = _repositorySource.GetRepositoryUrls(repository).First();
                    // may have .git suffix which must not be used with some of the properties which we must set
                    if (githubWebUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                        githubWebUrl = githubWebUrl.Substring(0, githubWebUrl.Length - ".git".Length);

                    // manually updated a build to new repository source and compared differences
                    var apiUrl = githubWebUrl.Replace("https://github.com", "https://api.github.com/repos");
                    var userNameAndRepo = $"{gh.UserName}/{repository.Name}";
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                    jObject["repository"]["id"] = userNameAndRepo;
                    jObject["repository"]["name"] = userNameAndRepo;
                    jObject["repository"]["url"] = githubWebUrl + ".git";
                    jObject["repository"]["properties"]["apiUrl"] = apiUrl;
                    jObject["repository"]["properties"]["branchesUrl"] = apiUrl + "/branches";
                    jObject["repository"]["properties"]["cloneUrl"] = githubWebUrl + ".git";
                    jObject["repository"]["properties"]["fullName"] = userNameAndRepo;
                    jObject["repository"]["properties"]["manageUrl"] = githubWebUrl;
                    jObject["repository"]["properties"]["refsUrl"] = apiUrl + "/git/refs";
                    jObject["repository"]["properties"]["safeRepository"] = userNameAndRepo;
                    jObject["repository"]["properties"]["shortName"] = repository.Name;
                    jObject["name"] = _config.BuildNamePrefix + repository.Name;
#pragma warning restore CS8602 // Dereference of a possibly null reference.
                    break;
                case "dev.azure.com":
                    var ado = (AzureDevOpsRepositoryTarget)_repositorySource;

                    if (!(repository is AzureDevOpsRepository adoRepo))
                        throw new NotSupportedException($"Received unsupported repository {repository.GetType()} from dev.azure.com");

                    var project = adoRepo.Project;

#pragma warning disable CS8602 // Dereference of a possibly null reference.
                    jObject["repository"]["id"] = adoRepo.Id;
                    jObject["repository"]["name"] = repository.Name;
                    jObject["repository"]["url"] = _repositorySource.GetRepositoryUrls(repository).First();
                    jObject["name"] = _config.BuildNamePrefix + project + " - " + repository.Name;
#pragma warning restore CS8602 // Dereference of a possibly null reference.
                    break;
                default:
                    throw new NotSupportedException($"Currently {_repositorySource.Type} is not supported as a repository source!");
            }

            var json = jObject.ToString();
            var response = await HttpClient.PostAsync($"https://dev.azure.com/{DevOpsOrganization}/{DevOpsProject}/_apis/build/definitions?api-version=5.1", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);
            var createdBuildJson = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Failed to create build. Response: " + createdBuildJson);

            var build = JsonSerializer.Deserialize<Build>(createdBuildJson, JsonSettings.Default) ?? throw new Exception("compiler");

            _log.LogInformation($"Queue initial build to mirror {repository.Name}");
            json = JsonSerializer.Serialize(new
            {
                definition = new
                {
                    id = build.Id
                }
            }, JsonSettings.Default);
            response = await HttpClient.PostAsync($"https://dev.azure.com/{DevOpsOrganization}/{DevOpsProject}/_apis/build/builds?api-version=5.1", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        private async Task<JsonElement> GetBuildDefinitionAsync(int id, CancellationToken cancellationToken)
        {
            var response = await HttpClient.GetAsync($"https://dev.azure.com/{DevOpsOrganization}/{DevOpsProject}/_apis/build/definitions/{id}?api-version=5.1");
            response.EnsureSuccessStatusCode();
            return await JsonSerializer.DeserializeAsync<JsonElement>(await response.Content.ReadAsStreamAsync(), JsonSettings.Default, cancellationToken);
        }

        private Task<Build[]> GetBuildsAsync(CancellationToken cancellationToken)
            => GetCollectionAsync<Build>($"https://dev.azure.com/{DevOpsOrganization}/{DevOpsProject}/_apis/build/definitions?api-version=5.1", cancellationToken);

        private class Build
        {
            public string Name { get; set; } = "";

            public int Id { get; set; }

            public BuildRepository Repository { get; set; } = new BuildRepository();
        }

        private class BuildRepository
        {
            public string Url { get; set; } = "";

            public string Name { get; set; } = "";
        }
    }
}

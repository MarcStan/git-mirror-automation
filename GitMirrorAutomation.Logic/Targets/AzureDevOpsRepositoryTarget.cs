using GitMirrorAutomation.Logic.Config;
using GitMirrorAutomation.Logic.Helpers;
using GitMirrorAutomation.Logic.Models;
using GitMirrorAutomation.Logic.Sources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitMirrorAutomation.Logic.Targets
{
    public class AzureDevOpsRepositoryTarget : AzureDevOpsBase, IRepositoryTarget
    {
        private readonly string[] _projectsToIgnore = new string[0];
        private readonly string[] _repositoriesToIgnore = new string[0];

        public AzureDevOpsRepositoryTarget(SourceConfig source)
            : base(source.Source, source.AccessToken)
        {
            _projectsToIgnore = source.ProjectsToIgnore;
            _repositoriesToIgnore = source.RepositoriesToIgnore;
        }

        public AzureDevOpsRepositoryTarget(string url, AccessToken accessToken)
            : base(url, accessToken)
        {
        }

        public string Type => "dev.azure.com";
        public string SourceId => $"{Type}/{DevOpsOrganization}/{DevOpsProject ?? "*"}";
        public string TargetId => SourceId;

        public async Task CreateRepositoryAsync(IRepositorySource source, IRepository repository, CancellationToken cancellationToken)
        {
            await EnsureAccessToken(cancellationToken);
            var json = JsonSerializer.Serialize(new Repository
            {
                Name = repository.Name,
            });

            var project = DevOpsProject;
            if (project == null)
            {
                if (!(repository is AzureDevOpsRepository adoRepo))
                    throw new NotSupportedException($"Cannot create DevOps repository from {repository.GetType()}");

                project = adoRepo.Project;

                if (!(source is AzureDevOpsRepositoryTarget adoSource))
                    throw new NotSupportedException($"Can only mirror entire Azure DevOps organization to another Azure DevOps organization, but found source: {source.GetType()}");
                // may be missing in target org
                await CreateProjectAsync(adoSource, adoRepo, cancellationToken);
            }
            var response = await HttpClient.PostAsync($"https://dev.azure.com/{DevOpsOrganization}/{project}/_apis/git/repositories?api-version=5.1", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public async Task<IRepository[]> GetRepositoriesAsync(CancellationToken cancellationToken)
        {
            await EnsureAccessToken(cancellationToken);
            if (DevOpsProject != null)
                return (await GetCollectionAsync<AzureDevOpsRepository>($"https://dev.azure.com/{DevOpsOrganization}/{DevOpsProject}/_apis/git/repositories?api-version=5.1", cancellationToken))
                    .Where(r => !_repositoriesToIgnore.Contains(r.Name))
                    .ToArray();

            var repos = new List<IRepository>();
            foreach (var project in (await GetProjectsAsync(cancellationToken))
                .Where(p => !_projectsToIgnore.Contains(p.Name)))
            {
                repos.AddRange((await GetCollectionAsync<AzureDevOpsRepository>($"https://dev.azure.com/{DevOpsOrganization}/{project.Name}/_apis/git/repositories?api-version=5.1", cancellationToken))
                    .Where(r => !_repositoriesToIgnore.Contains(r.Name)));
            }
            return repos.ToArray();
        }

        public string[] GetRepositoryUrls(IRepository repository)
        {
            var project = DevOpsProject ?? (repository is AzureDevOpsRepository ado ?
                            ado.Project :
                            throw new NotSupportedException($"Unsupported source {repository.GetType()}"));
            return new[]
            {
                $"https://dev.azure.com/{DevOpsOrganization}/{project}/_git/{repository.Name}",
                $"https://{DevOpsOrganization}@dev.azure.com/{DevOpsOrganization}/{project}/_git/{repository.Name}"
            };
        }

        private Task<Project[]> GetProjectsAsync(CancellationToken cancellationToken)
            => GetCollectionAsync<Project>($"https://dev.azure.com/{DevOpsOrganization}/_apis/projects?api-version=5.1", cancellationToken);

        private async Task CreateProjectAsync(AzureDevOpsRepositoryTarget source, AzureDevOpsRepository repo, CancellationToken cancellationToken)
        {
            var targetProjects = await GetProjectsAsync(cancellationToken);
            var matched = targetProjects.FirstOrDefault(p => p.Name == repo.Project);
            if (matched != null)
                return;

            // get project from source again to also mirror its visibility & description
            var sourceProjects = await source.GetProjectsAsync(cancellationToken);
            var toClone = sourceProjects.Single(p => p.Name == repo.Project);
            // properties are stored separate

            // first get project to get id (not set in list)
            var response = await source.HttpClient.GetAsync($"https://dev.azure.com/{source.DevOpsOrganization}/_apis/projects/{toClone.Name}?api-version=5.1", cancellationToken);
            response.EnsureSuccessStatusCode();
            var sourceProject = await JsonSerializer.DeserializeAsync<Project>(await response.Content.ReadAsStreamAsync(), JsonSettings.Default, cancellationToken) ?? throw new NotSupportedException("missing project");
            // with project id we can query project properties
            // api is eternally in preview
            var properties = await source.GetCollectionAsync<NameValue>($"https://dev.azure.com/{source.DevOpsOrganization}/_apis/projects/{sourceProject.Id}/properties?api-version=5.1-preview.1", cancellationToken);
            // fallback to hardcoded default project template id
            var correctProcess = properties.FirstOrDefault(p => p.Name == "System.ProcessTemplateType")?.Value.GetString() ?? "b8a3a935-7e91-48b8-a94c-606d37c3e9f2";
            var json = JsonSerializer.Serialize(new
            {
                name = toClone.Name,
                description = toClone.Description,
                visibility = toClone.Visibility,
                capabilities = new
                {
                    versioncontrol = new
                    {
                        sourceControlType = "Git"
                    },
                    processTemplate = new
                    {
                        templateTypeId = correctProcess
                    }
                }
            }, JsonSettings.Default);
            response = await HttpClient.PostAsync($"https://dev.azure.com/{DevOpsOrganization}/_apis/projects?api-version=5.1", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);
            response.EnsureSuccessStatusCode();
            // should wait for project creation as it takes some time. if we don't then git repo creation fails
            // don't bother because function runs every 5 mins -> repo will be created soon after again until it succeeds
        }

        private class Project
        {
            public string Id { get; set; } = "";

            public string Name { get; set; } = "";

            public string Visibility { get; set; } = "private";

            public string Description { get; set; } = "";
        }

        public class NameValue
        {
            public string Name { get; set; } = "";

            public JsonElement Value { get; set; } = new JsonElement();
        }
    }
}

using GitMirrorAutomation.Logic.Config;
using GitMirrorAutomation.Logic.Extensions;
using GitMirrorAutomation.Logic.Mirrors;
using GitMirrorAutomation.Logic.Sources;
using GitMirrorAutomation.Logic.Targets;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text.Json;

namespace GitMirrorAutomation.Logic
{
    public class ConfigurationParser
    {
        private readonly ILogger _log;

        public ConfigurationParser(
            ILogger log)
        {
            _log = log;
        }

        public IRepositorySource GetRepositorySource(JsonElement source)
        {
            if (source.ValueKind == JsonValueKind.String)
            {
                var url = source.GetString()!;
                switch (new Uri(url).Host.ToLowerInvariant())
                {
                    case "github.com":
                        return new GithubRepositorySource(url);
                    default:
                        throw new NotSupportedException($"Unsupported source {source}");
                }
            }
            else
            {
                var cfg = source.ToObject<SourceConfig>();
                return new AzureDevOpsRepositoryTarget(cfg);
            }
        }

        public IMirrorService GetMirrorService(MirrorViaConfig mirrorConfig, IRepositorySource source)
            => new Uri(mirrorConfig.Type).Host.ToLowerInvariant() switch
            {
                "dev.azure.com" => new AzurePipelinesMirror(mirrorConfig, source, _log),
                _ => throw new NotSupportedException($"Unsupported mirror {mirrorConfig.Type}")
            };

        public IRepositoryTarget[] GetRepositoryTargets(TargetConfig[] mirrorToConfig)
        {
            return mirrorToConfig.Select(GetRepositoryTarget).ToArray();
        }

        private IRepositoryTarget GetRepositoryTarget(TargetConfig mirrorToConfig)
            => new Uri(mirrorToConfig.Target).Host.ToLowerInvariant() switch
            {
                "gitlab.com" => new GitlabRepositoryTarget(mirrorToConfig),
                "dev.azure.com" => new AzureDevOpsRepositoryTarget(mirrorToConfig.Target, mirrorToConfig.AccessToken),
                _ => throw new NotSupportedException($"Unsupported target {mirrorToConfig.Target}")
            };
    }
}

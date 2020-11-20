using GitMirrorAutomation.Logic;
using GitMirrorAutomation.Logic.Config;
using GitMirrorAutomation.Logic.Storage;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitMirrorAutomation.Func
{
    public static class Functions
    {
        /// <summary>
        /// Will perform a scan to check if any new repositories exist.
        /// </summary>
        [FunctionName("scan")]
        public static async Task ScanRepositoriesAsync(
          [TimerTrigger(Schedule.Every5Minutes, RunOnStartup = true)] TimerInfo timer,
          ILogger log,
          CancellationToken cancellationToken)
        {
            var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage") ?? throw new ArgumentNullException("Storage connection string not set");
            var storageProvider = new AzureBlobStorageProvider(connectionString, "configuration");

            var configurations = await LoadConfigFilesAsync(storageProvider, cancellationToken);
            var configParser = new ConfigurationParser(log);
            foreach (var cfg in configurations)
            {
                var automation = new MirrorAutomationLogic(log);
                var source = configParser.GetRepositorySource(cfg.Source);
                var mirrorService = configParser.GetMirrorService(cfg.MirrorViaConfig, source);
                var targets = configParser.GetRepositoryTargets(cfg.TargetConfig);
                log.LogInformation($"Processing source {source.SourceId} (to be replicated to {Join(", ", " and ", targets.Select(t => t.TargetId))})");
                await automation.ProcessAsync(source, mirrorService, targets, cancellationToken);
            }
        }

        private static string Join<T>(string separator, string lastSeparator, IEnumerable<T> items)
        {
            var values = items as T[] ?? items.ToArray();
            if (values.Length > 2)
                return string.Join(separator, values.Take(values.Length - 1)) + lastSeparator + values.Last();

            return string.Join(lastSeparator, values);
        }

        private static async Task<Configuration[]> LoadConfigFilesAsync(AzureBlobStorageProvider storageProvider, CancellationToken cancellationToken)
        {
            var files = await storageProvider.ListAsync("", cancellationToken);
            var configs = new List<Configuration>();
            foreach (var file in files)
            {
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                configs.Add(ParseConfiguration(await storageProvider.GetAsync(file, cancellationToken)));
            }
            return configs.ToArray();
        }

        private static Configuration ParseConfiguration(string json)
            => JsonSerializer.Deserialize<Configuration>(json, JsonSettings.Default) ?? throw new NotSupportedException("failed loading config");
    }
}

using GitMirrorAutomation.Logic.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GitMirrorAutomation.Logic.Helpers
{
    public abstract class AzureDevOpsBase
    {
        private static readonly Regex _devOpsRegex = new Regex(@"https:\/\/dev\.azure\.com\/([^/?&# ]+)/(\*|[^/?&#]+)");
        private readonly AccessToken _accessToken;

        protected AzureDevOpsBase(
            string type,
            AccessToken accessToken)
        {
            var match = _devOpsRegex.Match(type);
            if (!match.Success)
                throw new ArgumentException("Expected a valid DevOps organization url but got: " + type);

            DevOpsOrganization = match.Groups[1].Value;
            if (match.Groups[2].Value != "*")
                DevOpsProject = match.Groups[2].Value;

            HttpClient = new HttpClient();
            HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _accessToken = accessToken;
        }

        public string DevOpsOrganization { get; }

        /// <summary>
        /// Null if source is set to be the entire organization.
        /// </summary>
        public string? DevOpsProject { get; }

        protected HttpClient HttpClient { get; }

        protected async Task<T[]> GetCollectionAsync<T>(string url, CancellationToken cancellationToken)
        {
            string? continuationToken = null;
            var items = new List<T>();
            do
            {
                var nextUrl = url +
                    (continuationToken != null ? $"&continuationToken={continuationToken}" : "");
                var response = await HttpClient.GetAsync(nextUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var data = await JsonSerializer.DeserializeAsync<Collection<T>>(await response.Content.ReadAsStreamAsync(), JsonSettings.Default) ?? new Collection<T>();
                items.AddRange(data.Value);

                if (response.Headers.TryGetValues("x-ms-continuationtoken", out var values))
                    continuationToken = values.FirstOrDefault();

            } while (continuationToken != null);

            return items.ToArray();
        }

        protected async Task EnsureAccessToken(CancellationToken cancellationToken)
        {
            if (HttpClient.DefaultRequestHeaders.Authorization != null)
                return;

            var token = await new AccessTokenHelper().GetAsync(_accessToken, cancellationToken);
            HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Format("{0}:{1}", "", token))));
        }

        private class Collection<T>
        {
            public T[] Value { get; set; } = new T[0];
        }
    }
}

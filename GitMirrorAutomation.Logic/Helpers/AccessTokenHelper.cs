using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using GitMirrorAutomation.Logic.Config;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GitMirrorAutomation.Logic.Helpers
{
    public class AccessTokenHelper
    {
        private static readonly Regex _keyVaultRegex = new Regex(@"https:\/\/([^.])+\.vault\.azure\.net");

        public async Task<string> GetAsync(AccessToken accessToken, CancellationToken cancellationToken)
        {
            var match = _keyVaultRegex.Match(accessToken.Source);
            if (!match.Success)
                throw new ArgumentException("Currently only keyvault source is supported but found: " + accessToken.Source);

            var client = new SecretClient(new Uri(accessToken.Source), new DefaultAzureCredential());
            var secret = await client.GetSecretAsync(accessToken.SecretName, null, cancellationToken);
            return secret.Value.Value;
        }
    }
}

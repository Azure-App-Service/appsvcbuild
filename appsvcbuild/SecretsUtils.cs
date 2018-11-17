using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using LibGit2Sharp;
using System.IO;
using LibGit2Sharp.Handlers;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Rest.Azure;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.Storage;
using Microsoft.Azure.Management.ContainerRegistry;
using Microsoft.Azure.Management.ContainerRegistry.Models;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.KeyVault;
using System.Xml.Linq;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using SendGrid;
using SendGrid.Helpers.Mail;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;


namespace appsvcbuild
{
    public class SecretsUtils
    {
        public String _gitToken { get; set; }
        public String _clientId { get; set; }
        public String _clientSecret { get; set; }
        public String _tenantId { get; set; }
        public String _subId { get; set; }
        public String _sendGridApiKey { get; set; }
        public String _appsvcbuildfuncMaster { get; set; }
        public AzureCredentials _credentials { get; set; }

        public SecretsUtils() {
        }

        public async System.Threading.Tasks.Task GetSecrets() {
            _clientId = GetAppSetting("clientId");
            _clientSecret = GetAppSetting("clientSecret");
            _tenantId = GetAppSetting("tenantId");
            _subId = GetAppSetting("subId");

            _credentials = SdkContext.AzureCredentialsFactory.FromServicePrincipal(_clientId,
                                                                                   _clientSecret,
                                                                                   _tenantId,
                                                                                   AzureEnvironment.AzureGlobalCloud);

            KeyVaultClient kvClient = new KeyVaultClient(
                async (string authority, string resource, string scope) =>
                {
                    AuthenticationContext authContext = new AuthenticationContext(authority);
                    ClientCredential clientCred = new ClientCredential(_clientId, _clientSecret);
                    AuthenticationResult result = await authContext.AcquireTokenAsync(resource, clientCred);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to retrieve access token for Key Vault");
                    }

                    return result.AccessToken;
                });
            _gitToken = (await kvClient.GetSecretAsync("https://appsvcbuild-vault.vault.azure.net/", "gitToken")).Value;
            _sendGridApiKey = (await kvClient.GetSecretAsync("https://appsvcbuild-vault.vault.azure.net/", "sendGridApiKey")).Value;
            _appsvcbuildfuncMaster = (await kvClient.GetSecretAsync("https://appsvcbuild-vault.vault.azure.net/", "appsvcbuildfuncMaster")).Value;

            if (_clientId == "")
            {
                throw new Exception("missing appsetting clientId ");
            }
            if (_clientSecret == "")
            {
                throw new Exception("missing appsetting clientSecret ");
            }
            if (_tenantId == "")
            {
                throw new Exception("missing appsetting tenantId ");
            }
            if (_subId == "")
            {
                throw new Exception("missing appsetting subId ");
            }
            if (_gitToken == "")
            {
                throw new Exception("missing setting gitToken in keyvault");
            }
            if (_sendGridApiKey == "")
            {
                throw new Exception("missing setting gitToken in sendGridApiKey");
            }
            if (_appsvcbuildfuncMaster == "")
            {
                throw new Exception("missing setting gitToken in appsvcbuildfuncMaster");
            }
            return;
        }

        private string GetAppSetting(string name)
        {
            return System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        }
    }
}

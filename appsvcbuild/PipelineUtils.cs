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
using RestSharp;
using NameValuePair = Microsoft.Azure.Management.WebSites.Models.NameValuePair;

namespace appsvcbuild
{
    public class PipelineUtils
    {
        public ILogger _log { get; set; }
        private ContainerRegistryManagementClient _registryClient;
        private WebSiteManagementClient _webappClient;
        private String _subscriptionID;

        private String _rgName = "appsvcbuildrg";
        private String _acrName = "appsvcbuildacr";

        public PipelineUtils(ContainerRegistryManagementClient registryClient, WebSiteManagementClient webappClient, String subscriptionID)
        {
            _registryClient = registryClient;
            _registryClient.SubscriptionId = subscriptionID;
            _webappClient = webappClient;
            _webappClient.SubscriptionId = subscriptionID;
        }


        public void CreateWebhook(String cdUrl, String webhookName, String imageName)
        {
            //_log.Info("creating webhook: " + webhookName);
            Registry reg = _registryClient.Registries.Get(_rgName, _acrName);

            _registryClient.Webhooks.Create(_rgName, _acrName, webhookName, new WebhookCreateParameters
            {
                Location = "westus2",
                Actions = new List<string>() { WebhookAction.Push },
                ServiceUri = cdUrl,
                Status = WebhookStatus.Enabled,
                Scope = imageName
            });
        }

        public String CreateTask(String taskName, String gitPath, String gitToken, String imageName, String authToken)
        {
            //_log.Info("creating task: " + taskName);

            RestClient client = new RestClient("https://dev.azure.com/patle/23b82bfb-5bab-4c97-8e1a-1ae8d771e222/_apis/build/builds?api-version=5.0");
            var request = new RestRequest(Method.POST);
            request.AddHeader("cache-control", "no-cache");
            request.AddHeader("Authorization", $"Basic {authToken}");
            request.AddHeader("Content-Type", "application/json");
            String body =
                $@"{{
                    ""queue"": {{
                        ""id"": 8
                    }},
                    ""definition"": {{
                        ""id"": 2
                    }},
                    ""project"": {{
                        ""id"": ""23b82bfb-5bab-4c97-8e1a-1ae8d771e222""
                    }},
                    ""sourceBranch"": ""master"",
                    ""sourceVersion"": """",
                    ""reason"": ""manual"",
                    ""demands"": [],
                    ""parameters"": ""{{
                        \""gitURL\"":\""{gitPath}\"",
                        \""imageTag\"":\""{imageName}\""
                    }}""
                }}";
            request.AddParameter("undefined", body, ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);

            var json = JsonConvert.DeserializeObject<dynamic>(response.Content.ToString());
            String runId = json.id;
            runId = runId.Replace("\"", "");

            client = new RestClient($"https://dev.azure.com/patle/23b82bfb-5bab-4c97-8e1a-1ae8d771e222/_apis/build/builds/{runId}?api-version=5.0");
            request = new RestRequest(Method.GET);
            request.AddHeader("Authorization", $"Basic {authToken}");

            while (true)
            {
                //_log.Info("run status : " + run.Status);
                response = client.Execute(request);
                json = JsonConvert.DeserializeObject<dynamic>(response.Content.ToString());
                var status = json.status;
                var result = json.result;
                if (status.ToString().ToLower().Equals("completed"))
                {
                    if (result.ToString().ToLower().Equals("succeeded"))
                    {
                        break;
                    }
                    throw new Exception($"run failed, id: {runId} message: {result}");
                }
                System.Threading.Thread.Sleep(10 * 1000);  // 10 sec
            }

            return "";
        }

        public string CreateWebapp(String version, String acrPassword, String appName, String imageName, String planName)
        {
            //_log.Info("creating webapp");

            _webappClient.WebApps.Delete(_rgName, appName, false, false);
            AppServicePlan plan = _webappClient.AppServicePlans.Get(_rgName, planName);

            //_log.Info("creating site :" + appName);
            _webappClient.WebApps.CreateOrUpdate(_rgName, appName,
                new Site
                {
                    Location = "westus2",
                    ServerFarmId = plan.Id,
                    SiteConfig = new SiteConfig
                    {
                        LinuxFxVersion = String.Format("DOCKER|{0}.azurecr.io/{1}", _acrName, imageName),
                        AppSettings = new List<NameValuePair>
                        {
                            new NameValuePair("DOCKER_REGISTRY_SERVER_USERNAME", _acrName),
                            new NameValuePair("DOCKER_REGISTRY_SERVER_PASSWORD", acrPassword),
                            new NameValuePair("DOCKER_REGISTRY_SERVER_URL", string.Format("https://{0}.azurecr.io", _acrName)),
                            new NameValuePair("DOCKER_ENABLE_CI", "false"),
                            new NameValuePair("WEBSITES_ENABLE_APP_SERVICE_STORAGE", "false")
                        }
                    }
                });

            User user = _webappClient.WebApps.ListPublishingCredentials(_rgName, appName);
            String cdUrl = String.Format("{0}/docker/hook", user.ScmUri);
            return cdUrl;
        }

        public string DeleteWebapp(String appName, String planName)
        {
            //_log.Info("creating webapp");

            _webappClient.WebApps.Delete(_rgName, appName, false, false);
            return "";
        }


        public String DeleteImage(String acr, String repo, String tag, String username, String password)
        {
            String path = String.Format("https://{0}.azurecr.io/acr/v1/{1}/_tags/{2}", acr, repo, tag);
            var client = new RestClient(path);
            var request = new RestRequest(Method.DELETE);
            request.AddHeader("cache-control", "no-cache");
            String token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(String.Format("{0}:{1}", username, password)));
            request.AddHeader("Authorization", String.Format("Basic {0}", token));
            IRestResponse response = client.Execute(request);
            return "";
        }
    }
}

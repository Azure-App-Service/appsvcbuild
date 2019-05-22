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
    public class PipelineUtils
    {
        public ILogger _log { get; set; }
        private ContainerRegistryManagementClient _registryClient;
        private WebSiteManagementClient _webappClient;
        private String _subscriptionID;

        private String _rgName = "appsvcbuildrg";
        private String _acrName = "appsvcbuildacr";

        public PipelineUtils(ContainerRegistryManagementClient registryClient, WebSiteManagementClient webappClient, String subscriptionID) {
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

        public String CreateTask(String taskName, String gitPath, String gitToken, String imageName)
        {
            //_log.Info("creating task: " + taskName);


            Registry reg = _registryClient.Registries.Get(_rgName, _acrName);

            Microsoft.Azure.Management.ContainerRegistry.Models.Task task = new Microsoft.Azure.Management.ContainerRegistry.Models.Task(
                       location: reg.Location,
                       platform: new PlatformProperties { Architecture = Architecture.Amd64, Os = OS.Linux },
                       step: new DockerBuildStep(
                           dockerFilePath: "Dockerfile",
                           contextPath: gitPath,
                           imageNames: new List<string> { imageName },
                           isPushEnabled: true,
                           noCache: true),
                       name: taskName,
                       status: Microsoft.Azure.Management.ContainerRegistry.Models.TaskStatus.Enabled,
                       timeout: 3 * 60 * 60, // 3 hours
                       /*trigger: new TriggerProperties(
                           sourceTriggers: new List<SourceTrigger> {
                               new SourceTrigger(
                                   sourceRepository: new SourceProperties(
                                       sourceControlType: SourceControlType.Github,
                                       repositoryUrl: gitPath,
                                       branch: "master",
                                       sourceControlAuthProperties: new AuthInfo(TokenType.PAT,
                                            gitToken,
                                            scope:"repo")),
                                   sourceTriggerEvents: new List<string>{ SourceTriggerEvent.Commit },
                                   name: "defaultSourceTriggerName",
                                   status: TriggerStatus.Enabled) },
                           baseImageTrigger: new BaseImageTrigger(
                               BaseImageTriggerType.Runtime,
                               "defaultBaseimageTriggerName",
                               TriggerStatus.Enabled)),*/
                       agentConfiguration: new AgentProperties(cpu: 2)
                       );
            task.Validate();

            _registryClient.Tasks.Create(_rgName, _acrName, taskName, task);

            //_log.Info("running task");
            Run run = _registryClient.Registries.ScheduleRun(_rgName, _acrName, new TaskRunRequest(taskName));

            //_log.Info("Run ID: " + run.RunId);
            List<String> waitingStatus = new List<String>();
            
            waitingStatus.Add(RunStatus.Queued);
            waitingStatus.Add(RunStatus.Started);
            waitingStatus.Add(RunStatus.Running);
            int sleepTime = 1;//1 second
            while (waitingStatus.Contains(run.Status))
            {
                //_log.Info("run status : " + run.Status);
                run = _registryClient.Runs.Get(_rgName, _acrName, run.RunId);
                System.Threading.Thread.Sleep(sleepTime * 1000);  //milliseconds
                sleepTime = sleepTime * 2;
            }
            if (run.Status != RunStatus.Succeeded)
            {
                throw new Exception(String.Format("Run Failed {0} {1} {2}", run.Id, run.Name, run.Task));
            }

            //_log.Info("run status : " + run.Status);
            RegistryListCredentialsResult registryCreds = _registryClient.Registries.ListCredentials(_rgName, _acrName);
            String acrPassword = registryCreds.Passwords[0].Value;

            return acrPassword;
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
                            new NameValuePair("DOCKER_ENABLE_CI", "true"),
                            new NameValuePair("WEBSITES_ENABLE_APP_SERVICE_STORAGE", "false")
                        }
                    }
                });

            User user = _webappClient.WebApps.ListPublishingCredentials(_rgName, appName);
            String cdUrl = String.Format("{0}/docker/hook", user.ScmUri);
            return cdUrl;
        }
    }
}

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

        private static String getDotnetcoreTemplate(String version)
        {
            if (version.StartsWith("1"))
            {
                return "debian-8";
            }
            else
            {
                return "debian-9";
            }
        }

        private static String getNodeTemplate(String version)
        {
            String[] versionNumbers = version.Split('.');
            // node 4
            if (Int32.Parse(versionNumbers[0]) == 4)
            {
                return "4-debian";
            }
            //8.0 and 8.1
            else if ((Int32.Parse(versionNumbers[0]) == 8) && (Int32.Parse(versionNumbers[1]) == 0 || Int32.Parse(versionNumbers[1]) == 1))
            {
                return "8.0-debian";
            }
            else //8.11+
            {
                return "debian-9";
            }
        }

        private static String getPhpTemplate(String version)
        {

            if (new List<String> { "5.6", "7.0", "7.2", "7.3" }.Contains(version))
            {
                return String.Format("template-{0}-apache", version);
            }

            throw new Exception(String.Format("unexpected php version: {0}", version));
        }

        private static String getPythonTemplate(String version)
        {
            if (new List<String>{"2.7", "3.6", "3.7" }.Contains(version))
            {
                return String.Format("template-{0}", version);
            }
            
            throw new Exception(String.Format("unexpected python version: {0}", version));
        }

        private static String getRubyTemplate(String version)
        {
            return "templates";
        }

        private static String getTemplate(String stack, String version)
        {
            if (stack == "dotnetcore")
            {
                return getDotnetcoreTemplate(version);
            }
            if (stack == "node")
            {
                return getNodeTemplate(version);
            }
            if (stack == "php")
            {
                return getPhpTemplate(version);
            }
            if (stack == "python")
            {
                return getPythonTemplate(version);
            }
            if (stack == "ruby")
            {
                return getRubyTemplate(version);
            }

            throw new Exception(String.Format("unexpected stack: {0}", stack));
        }

        public void processAddDefaults(List<BuildRequest> buildRequests)
        {
            foreach(BuildRequest br in buildRequests)
            {
                if (br.Stack == null)
                {
                    throw new Exception("missing stack");
                }
                br.Stack = br.Stack.ToLower();
                if (br.Version == null)
                {
                    throw new Exception("missing version");
                }
                if (br.TemplateRepoURL == null)
                {
                    br.TemplateRepoURL = String.Format("https://github.com/Azure-App-Service/{0}-template.git", br.Stack);
                }
                if (br.TemplateRepoName == null)
                {
                    br.TemplateRepoName = br.TemplateRepoURL.Substring(br.TemplateRepoURL.LastIndexOf("/") + 1).Replace(".git", "");
                }
                if (br.TemplateName == null)
                {
                    br.TemplateName = getTemplate(br.Stack, br.Version);
                }
                if (br.Branch == null)
                {
                    br.Branch = "master";
                }
                if (br.BaseImage == null)
                {
                    br.BaseImage = String.Format("mcr.microsoft.com/oryx/{0}-{1}:latest", br.Stack, br.Version);
                }
                if (br.OutputRepoURL == null)
                {
                    br.OutputRepoURL = String.Format("https://github.com/blessedimagepipeline/{0}-{1}.git", br.Stack, br.Version);
                }
                if (br.OutputRepoName == null)
                {
                    br.OutputRepoName = br.OutputRepoURL.Substring(br.OutputRepoURL.LastIndexOf("/") + 1).Replace(".git", "");
                }
                if (br.OutputImage == null)
                {
                    br.OutputImage = String.Format("{0}:{1}", br.Stack, br.Version);
                }
                if (br.TestWebAppName == null)
                {
                    br.TestWebAppName = String.Format("appsvcbuild-{0}-hostingstart-{1}-site", br.Stack, br.Version.Replace(".", "-"));
                }
            }
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
                       timeout: 3600,
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

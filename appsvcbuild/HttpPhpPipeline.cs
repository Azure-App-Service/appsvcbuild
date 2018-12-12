using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Azure.WebJobs.Host;
using LibGit2Sharp;
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
using System.Web.Http;
using Microsoft.ApplicationInsights;

namespace appsvcbuild
{
    public static class HttpPhpPipeline
    {
        private static ILogger _log;
        private static String _githubURL = "https://github.com/patricklee2/php-ci.git";

        private static SecretsUtils _secretsUtils;
        private static MailUtils _mailUtils;
        private static DockerhubUtils _dockerhubUtils;
        private static GitHubUtils _githubUtils;
        private static PipelineUtils _pipelineUtils;
        private static StringBuilder _emailLog;
        private static TelemetryClient _telemetry;

        [FunctionName("HttpPhpPipeline")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            _telemetry = new TelemetryClient();
            _telemetry.TrackEvent("HttpPhpPipeline started");
            await InitUtils(log);

            LogInfo("HttpPhpPipeline request received");

            String requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            List<String> newTags = data?.newTags.ToObject<List<String>>();

            if (newTags == null)
            {
                LogInfo("Failed: missing parameters `newTags` in body");
                await _mailUtils.SendFailureMail("Failed: missing parameters `newTags` in body", GetLog());
                return new BadRequestObjectResult("Failed: missing parameters `newTags` in body");
            }
            else if (newTags.Count == 0)
            {
                LogInfo("no new php tags found");
                await _mailUtils.SendSuccessMail(newTags, GetLog());
                return (ActionResult)new OkObjectResult($"no new php tags found");
            }
            else
            {
                try
                {
                    LogInfo($"HttpPhpPipeline executed at: { DateTime.Now }");
                    LogInfo(String.Format("new php tags found {0}", String.Join(", ", newTags)));
                
                    List<String> newVersions = MakePipeline(newTags, log);
                    await _mailUtils.SendSuccessMail(newVersions, GetLog());
                    return (ActionResult)new OkObjectResult($"built new php images: {String.Join(", ", newVersions)}");
                }
                catch (Exception e)
                {
                    LogInfo(e.ToString());
                    _telemetry.TrackException(e);
                    await _mailUtils.SendFailureMail(e.ToString(), GetLog());
                    return new InternalServerErrorResult();
                }
            }
        }

        public static void LogInfo(String message)
        {
            _emailLog.Append(message);
            _log.LogInformation(message);
            _telemetry.TrackEvent(message);
        }
        public static String GetLog()
        {
            return _emailLog.ToString();
        }

        public static async System.Threading.Tasks.Task InitUtils(ILogger log)
        {
            _emailLog = new StringBuilder();
            _secretsUtils = new SecretsUtils();
            await _secretsUtils.GetSecrets();
            _mailUtils = new MailUtils(new SendGridClient(_secretsUtils._sendGridApiKey), "Php");
            _dockerhubUtils = new DockerhubUtils();
            _githubUtils = new GitHubUtils(_secretsUtils._gitToken);
            _pipelineUtils = new PipelineUtils(
                new ContainerRegistryManagementClient(_secretsUtils._credentials),
                new WebSiteManagementClient(_secretsUtils._credentials),
                _secretsUtils._subId
                );

            _log = log;
            _mailUtils._log = log;
            _dockerhubUtils._log = log;
            _githubUtils._log = log;
            _pipelineUtils._log = log;
        }

        public static List<string> MakePipeline(List<String> newTags, ILogger log)
        {
            List<String> newVersions = new List<String>();
            foreach (String t in newTags)
            {
                newVersions.Add(t.Replace("-apache", ""));
            }

            foreach (String version in newVersions)
            {
                int tries = 3;
                while (true)
                {
                    try
                    {
                        tries--;
                        _mailUtils._version = version;
                        PushGithub(version);
                        CreatePhpPipeline(version);
                        LogInfo(String.Format("php {0} built", version));
                        break;
                    }
                    catch (Exception e)
                    {
                        LogInfo(e.ToString());
                        if (tries <= 0)
                        {
                            LogInfo(String.Format("php {0} failed", version));
                            throw e;
                        }
                        LogInfo("trying again");
                    }
                }
            }
            return newVersions;
        }

        public static void CreatePhpPipeline(String version)
        {
            CreatePhpHostingStartPipeline(version);
            CreatePhpAppPipeline(version);
        }

        public static void CreatePhpHostingStartPipeline(String version)
        {
            _log.LogInformation("creating pipeling for php hostingstart " + version);

            String githubPath = String.Format("https://github.com/patricklee2/php-{0}-apache", version);
            String phpVersionDash = version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-hostingstart-{0}-task", phpVersionDash);
            String appName = String.Format("appsvcbuild-php-hostingstart-{0}-site", phpVersionDash);
            String webhookName = String.Format("appsvcbuildphphostingstart{0}wh", version.Replace(".", ""));
            String imageName = String.Format("php:{0}-apache", version);
            String planName = "appsvcbuild-php-plan";

            _log.LogInformation("creating acr task for php hostingstart " + version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            _log.LogInformation("creating webapp for php hostingstart " + version);
            String cdUrl = _pipelineUtils.CreateWebapp(version, acrPassword, appName, imageName, planName);
            _pipelineUtils.CreateWebhook(cdUrl, webhookName, imageName);
        }

        public static void CreatePhpAppPipeline(String version)
        {
            _log.LogInformation("creating pipeling for php app " + version);

            String githubPath = String.Format("https://github.com/patricklee2/php-{0}-app-apache", version);
            String phpVersionDash = version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-{0}-app-task", phpVersionDash);
            String appName = String.Format("appsvcbuild-php-{0}-app-site", phpVersionDash);
            String webhookName = String.Format("appsvcbuildphpapp{0}wh", version.Replace(".", ""));
            String imageName = String.Format("phpapp:{0}-apache", version);
            String planName = "appsvcbuild-php-app-plan";

            _log.LogInformation("creating acr task for php app" + version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            _log.LogInformation("creating webapp for php app" + version);
            String cdUrl = _pipelineUtils.CreateWebapp(version, acrPassword, appName, imageName, planName);
            _pipelineUtils.CreateWebhook(cdUrl, webhookName, imageName);
        }

        private static String getTemplate(String version)
        {
            if (version.StartsWith("5.6"))
            {
                return "template-5.6-apache";
            }
            else if (version.StartsWith("7.0"))
            {
                return "template-7.0-apache";
            }
            else if (version.StartsWith("7.2"))
            {
                return "template-7.2-apache";
            }
            throw new Exception(String.Format("unexpected php version: {0}", version));
        }

        private static void PushGithub(String version)
        {
            _log.LogInformation("creating github files for php " + version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);
            String templateRepo = String.Format("{0}\\php-ci", parent);
            String phpRepo = String.Format("{0}\\php-{1}-apache", parent, version);
            String phpAppRepo = String.Format("{0}\\php-{1}-app-apache", parent, version);

            _githubUtils.Clone(_githubURL, templateRepo);
            _githubUtils.FillTemplate(
                templateRepo,
                String.Format("{0}\\{1}", templateRepo, getTemplate(version)),
                String.Format("{0}\\php-{1}-apache", templateRepo, version),
                String.Format("{0}\\php-{1}-apache\\DockerFile", templateRepo, version),
                String.Format("FROM php:{0}-apache\n", version),
                false);
            _githubUtils.FillTemplate(
                templateRepo,
                String.Format("{0}\\template-app-apache", templateRepo),
                String.Format("{0}\\php-{1}-app-apache", templateRepo, version),
                String.Format("{0}\\php-{1}-app-apache\\DockerFile", templateRepo, version),
                String.Format("FROM appsvcbuildacr.azurecr.io/php:{0}-apache\n", version),
                false);

            _githubUtils.CreateDir(phpRepo);
            _githubUtils.CreateDir(phpAppRepo);
            _githubUtils.DeepCopy(String.Format("{0}\\php-{1}-apache", templateRepo, version), phpRepo);
            _githubUtils.DeepCopy(String.Format("{0}\\php-{1}-app-apache", templateRepo, version), phpAppRepo);
            _githubUtils.InitGithubAsync(String.Format("php-{0}-apache", version));
            _githubUtils.InitGithubAsync(String.Format("php-{0}-app-apache", version));
            _githubUtils.Init(phpRepo);
            _githubUtils.Init(phpAppRepo);
            _githubUtils.AddRemote(phpRepo, String.Format("php-{0}-apache", version));
            _githubUtils.AddRemote(phpAppRepo, String.Format("php-{0}-app-apache", version));
            _githubUtils.Stage(phpRepo, "*");
            _githubUtils.Stage(phpAppRepo, "*");
            _githubUtils.CommitAndPush(phpRepo, String.Format("[appsvcbuild] Add php {0}", version));
            _githubUtils.CommitAndPush(phpAppRepo, String.Format("[appsvcbuild] Add php {0}", version));
            //_githubUtils.CleanUp(parent);
        }
    }
}

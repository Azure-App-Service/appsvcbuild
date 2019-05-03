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
    public static class HttpPythonPipeline
    {
        private static ILogger _log;
        private static String _githubURL = "https://github.com/patricklee2/python-template.git";

        private static SecretsUtils _secretsUtils;
        private static MailUtils _mailUtils;
        private static DockerhubUtils _dockerhubUtils;
        private static GitHubUtils _githubUtils;
        private static PipelineUtils _pipelineUtils;
        private static StringBuilder _emailLog;
        private static TelemetryClient _telemetry;

        [FunctionName("HttpPythonPipeline")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            _telemetry = new TelemetryClient();
            _telemetry.TrackEvent("HttpPythonPipeline started");
            await InitUtils(log);

            LogInfo("HttpPythonPipeline request received");

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
                LogInfo("no new python tags found");
                await _mailUtils.SendSuccessMail(newTags, GetLog());
                return (ActionResult)new OkObjectResult($"no new python tags found");
            }
            else
            {
                try
                {
                    LogInfo($"HttpPythonPipeline executed at: { DateTime.Now }");
                    LogInfo(String.Format("new python tags found {0}", String.Join(", ", newTags)));
                
                    List<String> newVersions = MakePipeline(newTags, log);
                    await _mailUtils.SendSuccessMail(newVersions, GetLog());
                    return (ActionResult)new OkObjectResult($"built new python images: {String.Join(", ", newVersions)}");
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
            _mailUtils = new MailUtils(new SendGridClient(_secretsUtils._sendGridApiKey), "Python");
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
                String version = t.Replace("python-", "").Split(':')[0];
                newVersions.Add(version);
                int tries = 3;
                {
                    try
                    {
                        tries--;
                        _mailUtils._version = version;
                        PushGithubAsync(t, version);
                        PushGithubAppAsync(t, version);
                        CreatePythonPipeline(version);

                        LogInfo(String.Format("python {0} built", version));
                        break;
                    }
                    catch (Exception e)
                    {
                        LogInfo(e.ToString());
                        if (tries <= 0)
                        {
                            LogInfo(String.Format("python {0} failed", version));
                            throw e;
                        }
                        LogInfo("trying again");
                    }
                }
            }
            return newVersions;
        }

        public static void CreatePythonPipeline(String version)
        {
            CreatePythonHostingStartPipeline(version);
            CreatePythonAppPipeline(version);
        }

        public static void CreatePythonHostingStartPipeline(String version)
        {
            _log.LogInformation("creating pipeling for python hostingstart " + version);

            String githubPath = String.Format("https://github.com/patricklee2/python-{0}", version);
            String pythonVersionDash = version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-python-hostingstart-{0}-task", pythonVersionDash);
            String appName = String.Format("appsvcbuild-python-hostingstart-{0}-site", pythonVersionDash);
            String webhookName = String.Format("appsvcbuildpythonhostingstart{0}wh", version.Replace(".", ""));
            String imageName = String.Format("python:{0}", version);
            String planName = "appsvcbuild-python-plan";

            _log.LogInformation("creating acr task for python hostingstart " + version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            _log.LogInformation("creating webapp for python hostingstart " + version);
            String cdUrl = _pipelineUtils.CreateWebapp(version, acrPassword, appName, imageName, planName);
            _pipelineUtils.CreateWebhook(cdUrl, webhookName, imageName);
        }

        public static void CreatePythonAppPipeline(String version)
        {
            _log.LogInformation("creating pipeling for python app " + version);

            String githubPath = String.Format("https://github.com/patricklee2/python-app-{0}", version);
            String pythonVersionDash = version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-python-app-{0}-task", pythonVersionDash);
            String appName = String.Format("appsvcbuild-python-app-{0}-site", pythonVersionDash);
            String webhookName = String.Format("appsvcbuildpythonapp{0}wh", version.Replace(".", ""));
            String imageName = String.Format("pythonapp:{0}", version);
            String planName = "appsvcbuild-python-app-plan";

            _log.LogInformation("creating acr task for python app" + version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            _log.LogInformation("creating webapp for python app" + version);
            String cdUrl = _pipelineUtils.CreateWebapp(version, acrPassword, appName, imageName, planName);
            _pipelineUtils.CreateWebhook(cdUrl, webhookName, imageName);
        }

        private static String getTemplate(String version)
        {
            if (version.StartsWith("2.7"))
            {
                return "template-2.7";
            }
            else if (version.StartsWith("3.6"))
            {
                return "template-3.6";
            }
            else if (version.StartsWith("3.7"))
            {
                return "template-3.7";
            }
            throw new Exception(String.Format("unexpected python version: {0}", version));
        }

        private static async void PushGithubAsync(String tag, String version)
        {
            String repoName = String.Format("python-{0}", version);

            _log.LogInformation("creating github files for python " + version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);

            String templateRepo = String.Format("{0}\\python-template", parent);
            String pythonRepo = String.Format("{0}\\{1}", parent, repoName);

            _githubUtils.Clone(_githubURL, templateRepo);
            _githubUtils.FillTemplate(
                templateRepo,
                String.Format("{0}\\{1}", templateRepo, getTemplate(version)),
                String.Format("{0}\\{1}", templateRepo, repoName),
                String.Format("{0}\\{1}\\DockerFile", templateRepo, repoName),
                new List<String> { String.Format("FROM oryxprod/{0}", tag) },
                new List<int> { 1 },
                false);

            _githubUtils.CreateDir(pythonRepo);
            if (await _githubUtils.RepoExistsAsync(repoName))
            {
                _githubUtils.Clone(
                    String.Format("https://github.com/patricklee2/{0}.git", repoName),
                    pythonRepo);
            }
            else
            {
                await _githubUtils.InitGithubAsync(repoName);
                _githubUtils.Init(pythonRepo);
                _githubUtils.AddRemote(pythonRepo, repoName);
            }

            _githubUtils.DeepCopy(String.Format("{0}\\{1}", templateRepo, repoName), pythonRepo);
            _githubUtils.Stage(pythonRepo, "*");
            _githubUtils.CommitAndPush(pythonRepo, String.Format("[appsvcbuild] Add python {0}", version));
            //_githubUtils.CleanUp(parent);
        }

        private static async void PushGithubAppAsync(String tag, String version)
        {
            String repoName = String.Format("python-app-{0}", version);

            _log.LogInformation("creating github files for python " + version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);

            String templateRepo = String.Format("{0}\\python-template", parent);
            String pythonRepo = String.Format("{0}\\{1}", parent, repoName);

            _githubUtils.Clone(_githubURL, templateRepo);
            _githubUtils.FillTemplate(
                templateRepo,
                String.Format("{0}\\template-app", templateRepo),
                String.Format("{0}\\{1}", templateRepo, repoName),
                String.Format("{0}\\{1}\\DockerFile", templateRepo, repoName),
                new List<String>{ String.Format("FROM appsvcbuildacr.azurecr.io/python:{0}\n", version) },
                new List<int> { 1 },
                false);

            _githubUtils.CreateDir(pythonRepo);
            if (await _githubUtils.RepoExistsAsync(repoName))
            {
                _githubUtils.Clone(
                    String.Format("https://github.com/patricklee2/{0}.git", repoName),
                    pythonRepo);
            }
            else
            {
                await _githubUtils.InitGithubAsync(repoName);
                _githubUtils.Init(pythonRepo);
                _githubUtils.AddRemote(pythonRepo, repoName);
            }

            _githubUtils.DeepCopy(String.Format("{0}\\{1}", templateRepo, repoName), pythonRepo);
            _githubUtils.Stage(pythonRepo, "*");
            _githubUtils.CommitAndPush(pythonRepo, String.Format("[appsvcbuild] Add python {0}", version));
            //_githubUtils.CleanUp(parent);
        }
    }
}

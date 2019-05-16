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
            List<BuildRequest> buildRequests = data?.buildRequests.ToObject<List<BuildRequest>>();
            _pipelineUtils.processAddDefaults(buildRequests);

            if (buildRequests == null)
            {
                LogInfo("Failed: missing parameters `newTags` in body");
                await _mailUtils.SendFailureMail("Failed: missing parameters `newTags` in body", GetLog());
                return new BadRequestObjectResult("Failed: missing parameters `newTags` in body");
            }
            else if (buildRequests.Count == 0)
            {
                LogInfo("no new python tags found");
                await _mailUtils.SendSuccessMail(new List<string> { "fix me later" }, GetLog());
                return (ActionResult)new OkObjectResult($"no new python tags found");
            }
            else
            {
                try
                {
                    LogInfo($"HttpPythonPipeline executed at: { DateTime.Now }");
                    LogInfo(String.Format("new python tags found {0}", String.Join(", ", buildRequests)));

                    List<String> newVersions = await MakePipeline(buildRequests, log);
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

        public static async Task<List<string>> MakePipeline(List<BuildRequest> buildRequests, ILogger log)
        {
            List<String> newVersions = new List<String>();

            foreach (BuildRequest br in buildRequests)
            {
                newVersions.Add(br.Version);
                int tries = 3;
                {
                    try
                    {
                        tries--;
                        _mailUtils._version = br.Version;
                        await PushGithubAsync(br);
                        await CreatePythonHostingStartPipeline(br);
                        await PushGithubAppAsync(br);
                        await CreatePythonAppPipeline(br);

                        LogInfo(String.Format("python {0} built", br.Version));
                        break;
                    }
                    catch (Exception e)
                    {
                        LogInfo(e.ToString());
                        if (tries <= 0)
                        {
                            LogInfo(String.Format("python {0} failed", br.Version));
                            throw e;
                        }
                        LogInfo("trying again");
                    }
                }
            }
            return newVersions;
        }
        
        public static async Task<Boolean> CreatePythonHostingStartPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for python hostingstart " + br.Version);

            String pythonVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-python-hostingstart-{0}-task", pythonVersionDash);
            String appName = br.TestWebAppName;
            String imageName = br.OutputImage;
            String planName = "appsvcbuild-python-plan";

            LogInfo("creating acr task for python hostingstart " + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.OutputRepoURL, _secretsUtils._gitToken, imageName);
            LogInfo("done creating acr task for python hostingstart " + br.Version);

            LogInfo("creating webapp for python hostingstart " + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, acrPassword, appName, imageName, planName);
            LogInfo("done creating webapp for python hostingstart " + br.Version);

            return true;
        }

        public static async Task<Boolean> CreatePythonAppPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for python app " + br.Version);

            String githubPath = String.Format("https://github.com/blessedimagepipeline/python-app-{0}", br.Version);
            String pythonVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-python-app-{0}-task", pythonVersionDash);
            String appName = String.Format("appsvcbuild-python-app-{0}-site", pythonVersionDash);
            String imageName = String.Format("pythonapp:{0}", br.Version);
            String planName = "appsvcbuild-python-app-plan";

            LogInfo("creating acr task for python app" + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            LogInfo("done creating acr task for python app" + br.Version);

            LogInfo("creating webapp for python app" + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, acrPassword, appName, imageName, planName);
            LogInfo("done creating webapp for python app" + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubAsync(BuildRequest br)
        {
            LogInfo("creating github files for python " + br.Version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.OutputRepoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, br.Branch);
            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.TemplateName),
                String.Format("{0}\\{1}", localTemplateRepoPath, br.OutputRepoName),
                false);
            _githubUtils.FillTemplate(
                String.Format("{0}\\{1}\\DockerFile", localTemplateRepoPath, br.OutputRepoName),
                new List<String> { String.Format("FROM {0}", br.BaseImage) },
                new List<int> { 1 });

            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(br.OutputRepoName))
            {
                _githubUtils.Clone(
                    br.OutputRepoURL,
                    localOutputRepoPath,
                    "master");
            }
            else
            {
                await _githubUtils.InitGithubAsync(br.OutputRepoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, br.OutputRepoName);
            }

            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.OutputRepoName),
                localOutputRepoPath,
                false);

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, String.Format("[appsvcbuild] Add python {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for python " + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubAppAsync(BuildRequest br)
        {
            String outputRepoName = String.Format("python-app-{0}", br.Version);

            LogInfo("creating github files for python app" + br.Version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\python-template", parent);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, outputRepoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, br.Branch);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(outputRepoName))
            {
                _githubUtils.Clone(
                    String.Format("https://github.com/blessedimagepipeline/{0}.git", outputRepoName),
                    localOutputRepoPath,
                    "master");
            }
            else
            {
                await _githubUtils.InitGithubAsync(outputRepoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, outputRepoName);
            }

            _githubUtils.DeepCopy(
                String.Format("{0}\\template-app", localTemplateRepoPath),
                localOutputRepoPath,
                false);
            _githubUtils.FillTemplate(
                String.Format("{0}\\DockerFile", localOutputRepoPath),
                new List<String>{ String.Format("FROM appsvcbuildacr.azurecr.io/{0}", br.OutputImage) },
                new List<int> { 1 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, String.Format("[appsvcbuild] Add python {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for python app" + br.Version);

            return true;
        }
    }
}

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
                LogInfo("no new node tags found");
                await _mailUtils.SendSuccessMail(new List<string> { "fix me later" }, GetLog());
                return (ActionResult)new OkObjectResult($"no new node tags found");
            }
            else
            {
                try
                {
                    LogInfo($"HttpPhpPipeline executed at: { DateTime.Now }");
                    LogInfo(String.Format("new php tags found {0}", String.Join(", ", buildRequests.ToString())));

                    List<String> newVersions = await MakePipeline(buildRequests, log);
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

        public static async Task<List<string>> MakePipeline(List<BuildRequest> buildRequests, ILogger log)
        {
            List<String> newVersions = new List<String>();


            foreach (BuildRequest br in buildRequests)
            {
                newVersions.Add(br.Version);
                int tries = 3;
                while (true)
                {
                    try
                    {
                        tries--;
                        _mailUtils._version = br.Version;
                        await PushGithubAsync(br);
                        await CreatePhpHostingStartPipeline(br);
                        await PushGithubAppAsync(br);
                        await CreatePhpAppPipeline(br);
                        LogInfo(String.Format("php {0} built", br.Version));
                        break;
                    }
                    catch (Exception e)
                    {
                        LogInfo(e.ToString());
                        if (tries <= 0)
                        {
                            LogInfo(String.Format("php {0} failed", br.Version));
                            throw e;
                        }
                        LogInfo("trying again");
                    }
                }
            }
            return newVersions;
        }

        private static String getTemplate(String version)
        {

            if (new List<String> { "5.6", "7.0", "7.2", "7.3" }.Contains(version))
            {
                return String.Format("template-{0}-apache", version);
            }

            throw new Exception(String.Format("unexpected php version: {0}", version));
        }

        public static async Task<Boolean> CreatePhpHostingStartPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for php hostingstart " + br.Version);

            String githubPath = br.TemplateRepoURL;
            String phpVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-hostingstart-{0}-task", phpVersionDash);
            String appName = br.TestWebAppName;
            String imageName = br.OutputImage;
            String planName = "appsvcbuild-php-plan";

            LogInfo("creating acr task for php hostingstart " + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            LogInfo("done reating acr task for php hostingstart " + br.Version);

            LogInfo("creating webapp for php hostingstart " + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, acrPassword, appName, imageName, planName);
            LogInfo("done creating webapp for php hostingstart " + br.Version); ;

            return true;
        }

        public static async Task<Boolean> CreatePhpAppPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for php app " + br.Version);

            String githubPath = String.Format("https://github.com/blessedimagepipeline/php-app-{0}-apache", br.Version);
            String phpVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-app-{0}-task", phpVersionDash);
            String appName = String.Format("appsvcbuild-php-app-{0}-site", phpVersionDash);
            String imageName = String.Format("phpapp:{0}-apache", br.Version);
            String planName = "appsvcbuild-php-app-plan";

            LogInfo("creating acr task for php app" + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            LogInfo("done creating acr task for php app" + br.Version);

            LogInfo("creating webapp for php app" + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, acrPassword, appName, imageName, planName);
            LogInfo("done creating webapp for php app" + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubAsync(BuildRequest br)
        {
            LogInfo("creating github files for php " + br.Version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.OutputRepoName);

            _githubUtils.Clone(br.Version, localTemplateRepoPath, br.Branch);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(br.OutputRepoName))
            {
                _githubUtils.Clone(
                    br.TemplateRepoURL,
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
                String.Format("{0}\\{1}", localTemplateRepoPath, br.TemplateName),
                localOutputRepoPath,
                false);
            _githubUtils.FillTemplate(
                String.Format("{0}\\DockerFile", localOutputRepoPath),
                new List<String>{ String.Format("FROM {0}", br.BaseImage), String.Format("ENV PHP_VERSION {0}", br.Version) },
                new List<int> { 1, 4 }
            );

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, String.Format("[appsvcbuild] Add php {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for php " + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubAppAsync(BuildRequest br)
        {
            String repoName = String.Format("php-app-{0}-apache", br.Version);

            LogInfo("creating github files for php app " + br.Version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\php-template", parent);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, repoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, br.Branch);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(repoName))
            {
                _githubUtils.Clone(
                    String.Format("https://github.com/blessedimagepipeline/{0}.git", repoName),
                    localOutputRepoPath,
                    "master");
            }
            else
            {
                await _githubUtils.InitGithubAsync(repoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, repoName);
            }

            _githubUtils.DeepCopy(
                String.Format("{0}\\template-app-apache", localTemplateRepoPath),
                localOutputRepoPath,
                false);
            _githubUtils.FillTemplate(
               String.Format("{0}\\DockerFile", localOutputRepoPath),
               new List<String> { String.Format("FROM appsvcbuildacr.azurecr.io/{0}", br.OutputImage) },
               new List<int> { 1 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, String.Format("[appsvcbuild] Add php {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for php app " + br.Version);

            return true;
        }
    }
}

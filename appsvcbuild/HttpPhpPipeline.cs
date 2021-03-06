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

        public static async Task<String> Run(BuildRequest br, ILogger log)
        {
            _telemetry = new TelemetryClient();
            _telemetry.TrackEvent("HttpPhpPipeline started");
            await InitUtils(log);

            LogInfo("HttpPhpPipeline request received");

            try
            {
                _mailUtils._buildRequest = br;
                LogInfo($"HttpPhpPipeline executed at: { DateTime.Now }");
                LogInfo(String.Format("new Php BuildRequest found {0}", br.ToString()));

                Boolean success = await MakePipeline(br, log);
                await _mailUtils.SendSuccessMail(new List<String> { br.Version }, GetLog());
                String successMsg =
                    $@"{{
                        ""status"": ""success"",
                        ""image"": ""appsvcbuildacr.azurecr.io/{br.OutputImageName}"",
                        ""webApp"": ""https://{br.WebAppName}.azurewebsites.net""
                    }}";
                return successMsg;
            }
            catch (Exception e)
            {
                LogInfo(e.ToString());
                _telemetry.TrackException(e);
                await _mailUtils.SendFailureMail(e.ToString(), GetLog());
                String failureMsg =
                    $@"{{
                        ""status"": ""failure"",
                        ""error"": ""{e.ToString()}""
                    }}";
                return failureMsg;
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

        public static async Task<Boolean> MakePipeline(BuildRequest br, ILogger log)
        {
            int tries = 3;
            while (true)
            {
                try
                {
                    tries--;
                    _mailUtils._version = br.Version;
                    LogInfo("Creating pipeline for Php " + br.Version);
                    await PushGithubAsync(br);
                    await CreatePhpHostingStartPipeline(br);
                    await PushGithubXdebugAsync(br);
                    await CreatePhpXdebugPipeline(br);
                    await PushGithubAppAsync(br);
                    await CreatePhpAppPipeline(br);
                    LogInfo(String.Format("Php {0} built", br.Version));
                    return true;
                }
                catch (Exception e)
                {
                    LogInfo(e.ToString());
                    if (tries <= 0)
                    {
                        LogInfo(String.Format("Php {0} failed", br.Version));
                        throw e;
                    }
                    LogInfo("trying again");
                    System.Threading.Thread.Sleep(1 * 60 * 1000);  //1 min
                }
            }
        }

        public static async Task<Boolean> CreatePhpHostingStartPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for php hostingstart " + br.Version);

            String phpVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-hostingstart-{0}-task", phpVersionDash);
            String planName = "appsvcbuild-php-plan";

            LogInfo("creating acr task for php hostingstart " + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.OutputRepoURL, _secretsUtils._gitToken, br.OutputImageName, _secretsUtils._pipelineToken);
            LogInfo("done reating acr task for php hostingstart " + br.Version);

            LogInfo("creating webapp for php hostingstart " + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, _secretsUtils._acrPassword, br.WebAppName, br.OutputImageName, planName);
            LogInfo("done creating webapp for php hostingstart " + br.Version);

            return true;
        }

        public static async Task<Boolean> CreatePhpXdebugPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for php app " + br.Version);

            String phpVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-app-{0}-task", phpVersionDash);

            LogInfo("creating acr task for php app" + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.XdebugOutputRepoURL, _secretsUtils._gitToken, br.XdebugOutputImageName, _secretsUtils._pipelineToken);
            LogInfo("done creating acr task for php app" + br.Version);

            return true;
        }

        public static async Task<Boolean> CreatePhpAppPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for php app " + br.Version);

            String phpVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-app-{0}-task", phpVersionDash);
            String planName = "appsvcbuild-php-app-plan";

            LogInfo("creating acr task for php app" + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.TestOutputRepoURL, _secretsUtils._gitToken, br.TestOutputImageName, _secretsUtils._pipelineToken);
            LogInfo("done creating acr task for php app" + br.Version);

            LogInfo("creating webapp for php app" + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, _secretsUtils._acrPassword, br.TestWebAppName, br.TestOutputImageName, planName);
            LogInfo("done creating webapp for php app" + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubAsync(BuildRequest br)
        {
            LogInfo("creating github files for php " + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String parent = String.Format("F:\\home\\site\\wwwroot\\appsvcbuild{0}{1}", timeStamp, random);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.OutputRepoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, br.TemplateRepoBranchName);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(br.OutputRepoOrgName, br.OutputRepoName))
            {
                _githubUtils.Clone(
                    br.OutputRepoURL,
                    localOutputRepoPath,
                    br.OutputRepoBranchName);
            }
            else
            {
                await _githubUtils.InitGithubAsync(br.OutputRepoOrgName, br.OutputRepoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, br.OutputRepoOrgName, br.OutputRepoName);
            }

            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.TemplateName),
                localOutputRepoPath,
                false);
            _githubUtils.FillTemplate(
                String.Format("{0}\\DockerFile", localOutputRepoPath),
                new List<String>{ String.Format("FROM {0}", br.BaseImageName), String.Format("ENV PHP_VERSION {0}", br.Version) },
                new List<int> { 1, 4 }
            );

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.OutputRepoBranchName, String.Format("[appsvcbuild] Add php {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for php " + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubXdebugAsync(BuildRequest br)
        {

            LogInfo("creating github files for php app " + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String parent = String.Format("F:\\home\\site\\wwwroot\\appsvcbuild{0}{1}", timeStamp, random);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.XdebugTemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.XdebugOutputRepoName);

            _githubUtils.Clone(br.XdebugTemplateRepoURL, localTemplateRepoPath, br.XdebugTemplateRepoBranchName);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(br.XdebugOutputRepoOrgName, br.XdebugOutputRepoName))
            {
                _githubUtils.Clone(
                   br.XdebugOutputRepoURL,
                   localOutputRepoPath,
                   br.XdebugOutputRepoBranchName);
            }
            else
            {
                await _githubUtils.InitGithubAsync(br.XdebugOutputRepoOrgName, br.XdebugOutputRepoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, br.XdebugOutputRepoOrgName, br.XdebugOutputRepoName);
            }

            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.XdebugTemplateName),
                localOutputRepoPath,
                false);
            _githubUtils.FillTemplate(
               String.Format("{0}\\DockerFile", localOutputRepoPath),
               new List<String> { String.Format("FROM appsvcbuildacr.azurecr.io/{0}", br.XdebugBaseImageName) },
               new List<int> { 1 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.XdebugOutputRepoBranchName, String.Format("[appsvcbuild] Add php {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for php app " + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubAppAsync(BuildRequest br)
        {

            LogInfo("creating github files for php app " + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String parent = String.Format("F:\\home\\site\\wwwroot\\appsvcbuild{0}{1}", timeStamp, random);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TestTemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.TestOutputRepoName);

            _githubUtils.Clone(br.TestTemplateRepoURL, localTemplateRepoPath, br.TestTemplateRepoBranchName);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(br.TestOutputRepoOrgName, br.TestOutputRepoName))
            {
                 _githubUtils.Clone(
                    br.TestOutputRepoURL,
                    localOutputRepoPath,
                    br.TestOutputRepoBranchName);
            }
            else
            {
                await _githubUtils.InitGithubAsync(br.TestOutputRepoOrgName, br.TestOutputRepoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, br.TestOutputRepoOrgName, br.TestOutputRepoName);
            }

            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.TestTemplateName),
                localOutputRepoPath,
                false);
            _githubUtils.FillTemplate(
               String.Format("{0}\\DockerFile", localOutputRepoPath),
               new List<String> { String.Format("FROM appsvcbuildacr.azurecr.io/{0}", br.TestBaseImageName) },
               new List<int> { 1 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.TestOutputRepoBranchName, String.Format("[appsvcbuild] Add php {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for php app " + br.Version);

            return true;
        }
    }
}

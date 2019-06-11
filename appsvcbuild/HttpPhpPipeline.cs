using Microsoft.ApplicationInsights;
using Microsoft.Azure.Management.ContainerRegistry;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Extensions.Logging;
using SendGrid;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

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
                        ""input"" : {JsonConvert.SerializeObject(br)}
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
                        ""error"": ""{e.ToString()}"",
                        ""input"" : {JsonConvert.SerializeObject(br)}
                    }}";
                return failureMsg;
            }
            finally
            {
                if (!br.SaveArtifacts)
                {
                    await DeletePipeline(br, log);
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

        public static async Task<Boolean> MakePipeline(BuildRequest br, ILogger log)
        {
            int tries = br.Tries;
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
        public static async Task<Boolean> DeletePipeline(BuildRequest br, ILogger log)
        {
            // delete github repo
            await _githubUtils.DeleteGithubAsync(br.OutputRepoOrgName, br.OutputRepoName);
            await _githubUtils.DeleteGithubAsync(br.XdebugOutputRepoOrgName, br.XdebugOutputRepoName);
            await _githubUtils.DeleteGithubAsync(br.TestOutputRepoOrgName, br.TestOutputRepoName);

            // delete acr image
            _pipelineUtils.DeleteImage(
                "appsvcbuildacr",
                br.OutputImageName.Split(':')[0],
                br.OutputImageName.Split(':')[1],
                "appsvcbuildacr",
                _secretsUtils._acrPassword
                );
            _pipelineUtils.DeleteImage(
                "appsvcbuildacr",
                br.XdebugOutputImageName.Split(':')[0],
                br.XdebugOutputImageName.Split(':')[1],
                "appsvcbuildacr",
                _secretsUtils._acrPassword
                );
            _pipelineUtils.DeleteImage(
                "appsvcbuildacr",
                br.TestOutputImageName.Split(':')[0],
                br.TestOutputImageName.Split(':')[1],
                "appsvcbuildacr",
                _secretsUtils._acrPassword
                );

            // delete webapp
            _pipelineUtils.DeleteWebapp(br.WebAppName, "appsvcbuild-php-plan");
            _pipelineUtils.DeleteWebapp(br.TestWebAppName, "appsvcbuild-php-app-plan");

            return true;
        }

        public static async Task<Boolean> CreatePhpHostingStartPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for php hostingstart " + br.Version);

            String phpVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-hostingstart-{0}-task", phpVersionDash);
            String planName = "appsvcbuild-php-plan";

            LogInfo("creating acr task for php hostingstart " + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.OutputRepoURL, br.OutputRepoBranchName, _secretsUtils._gitToken,
                br.OutputImageName, _secretsUtils._pipelineToken, useCache: br.UseCache);
            LogInfo("done reating acr task for php hostingstart " + br.Version);

            LogInfo("creating webapp for php hostingstart " + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, _secretsUtils._acrPassword, br.WebAppName, br.OutputImageName, planName);
            LogInfo("done creating webapp for php hostingstart " + br.Version);

            return true;
        }

        public static async Task<Boolean> CreatePhpXdebugPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for php xdebug " + br.Version);

            String phpVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-app-{0}-task", phpVersionDash);

            LogInfo("creating acr task for php app" + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.XdebugOutputRepoURL, br.XdebugOutputRepoBranchName, _secretsUtils._gitToken,
                br.XdebugOutputImageName, _secretsUtils._pipelineToken, useCache: br.UseCache);
            LogInfo("done creating acr task for php xdebug" + br.Version);

            return true;
        }

        public static async Task<Boolean> CreatePhpAppPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for php app " + br.Version);

            String phpVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-app-{0}-task", phpVersionDash);
            String planName = "appsvcbuild-php-app-plan";

            LogInfo("creating acr task for php app" + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.TestOutputRepoURL, br.TestOutputRepoBranchName, _secretsUtils._gitToken,
                br.TestOutputImageName, _secretsUtils._pipelineToken, useCache: br.UseCache);
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
            String parent = String.Format("D:\\local\\Temp\\appsvcbuild{0}{1}", timeStamp, random);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.OutputRepoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, br.TemplateRepoBranchName, br.PullRepo, br.PullId);
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
            _githubUtils.Checkout(localOutputRepoPath, br.OutputRepoBranchName);
            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.TemplateName),
                localOutputRepoPath);
            _githubUtils.FillTemplate(
                String.Format("{0}\\DockerFile", localOutputRepoPath),
                new List<String>{ String.Format("FROM {0}", br.BaseImageName), String.Format("ENV PHP_VERSION {0}", br.Version) },
                new List<int> { 1, 4 }
            );

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.OutputRepoBranchName, String.Format("[appsvcbuild] Add php {0}", br.Version));
            _githubUtils.gitDispose(localOutputRepoPath);
            _githubUtils.gitDispose(localTemplateRepoPath);
            _githubUtils.Delete(parent);
            LogInfo("done creating github files for php " + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubXdebugAsync(BuildRequest br)
        {

            LogInfo("creating github files for php xdebug " + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String parent = String.Format("D:\\local\\Temp\\appsvcbuild{0}{1}", timeStamp, random);
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
            _githubUtils.Checkout(localOutputRepoPath, br.OutputRepoBranchName);
            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.XdebugTemplateName),
                localOutputRepoPath);
            _githubUtils.FillTemplate(
               String.Format("{0}\\DockerFile", localOutputRepoPath),
               new List<String> { String.Format("FROM appsvcbuildacr.azurecr.io/{0}", br.XdebugBaseImageName) },
               new List<int> { 1 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.XdebugOutputRepoBranchName, String.Format("[appsvcbuild] Add php {0}", br.Version));
            _githubUtils.gitDispose(localOutputRepoPath);
            _githubUtils.gitDispose(localTemplateRepoPath);
            _githubUtils.Delete(parent);
            LogInfo("done creating github files for php xdebug " + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubAppAsync(BuildRequest br)
        {

            LogInfo("creating github files for php app " + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String parent = String.Format("D:\\local\\Temp\\appsvcbuild{0}{1}", timeStamp, random);
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
            _githubUtils.Checkout(localOutputRepoPath, br.OutputRepoBranchName);
            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.TestTemplateName),
                localOutputRepoPath);
            _githubUtils.FillTemplate(
               String.Format("{0}\\DockerFile", localOutputRepoPath),
               new List<String> { String.Format("FROM appsvcbuildacr.azurecr.io/{0}", br.TestBaseImageName) },
               new List<int> { 1 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.TestOutputRepoBranchName, String.Format("[appsvcbuild] Add php {0}", br.Version));
            _githubUtils.gitDispose(localOutputRepoPath);
            _githubUtils.gitDispose(localTemplateRepoPath);
            _githubUtils.Delete(parent);
            LogInfo("done creating github files for php app " + br.Version);

            return true;
        }
    }
}

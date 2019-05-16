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
    public static class HttpRubyPipeline
    {
        private static ILogger _log;
        private static SecretsUtils _secretsUtils;
        private static MailUtils _mailUtils;
        private static DockerhubUtils _dockerhubUtils;
        private static GitHubUtils _githubUtils;
        private static PipelineUtils _pipelineUtils;
        private static StringBuilder _emailLog;
        private static TelemetryClient _telemetry;

        [FunctionName("HttpRubyPipeline")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            _telemetry = new TelemetryClient();
            _telemetry.TrackEvent("HttpRubyPipeline started");
            await InitUtils(log);

            LogInfo("HttpRubyPipeline request received");

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
                LogInfo("no new ruby tags found");
                await _mailUtils.SendSuccessMail(new List<string> { "fix me later" }, GetLog());
                return (ActionResult)new OkObjectResult($"no new ruby tags found");
            }
            else
            {
                try
                {
                    LogInfo($"HttpRubyPipeline executed at: { DateTime.Now }");
                    LogInfo(String.Format("new ruby tags found {0}", String.Join(", ", buildRequests)));
                
                    List<String> newVersions = await MakePipeline(buildRequests, log);
                    await _mailUtils.SendSuccessMail(newVersions, GetLog());
                    return (ActionResult)new OkObjectResult($"built new ruby images: {String.Join(", ", newVersions)}");
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
            _mailUtils = new MailUtils(new SendGridClient(_secretsUtils._sendGridApiKey), "Ruby");
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
                        LogInfo("creating pipeling for ruby " + br.Version);
                        await PushGithubBaseAsync(br);
                        await CreateRubyBasePipeline(br);
                        await PushGithubHostingStartAsync(br);
                        await CreateRubyHostingStartPipeline(br);
                        LogInfo(String.Format("ruby {0} built", br.Version));
                        break;
                    }
                    catch (Exception e)
                    {
                        LogInfo(e.ToString());
                        if (tries <= 0)
                        {
                            LogInfo(String.Format("ruby {0} failed", br.Version));
                            throw e;
                        }
                        LogInfo("trying again");
                    }
                }
            }
            return newVersions;
        }

        public static async System.Threading.Tasks.Task CreateRubyBasePipeline(BuildRequest br)
        {
            String githubPath = String.Format("https://github.com/blessedimagepipeline/rubybase-{0}", br.Version);
            String rubyVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-ruby-base-{0}-task", rubyVersionDash);
            String imageName = String.Format("rubybase:{0}", br.Version);
            

            LogInfo("creating acr task for ruby base " + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            LogInfo("done creating acr task for ruby base " + br.Version);
            return;
        }

        public static async System.Threading.Tasks.Task CreateRubyHostingStartPipeline(BuildRequest br)
        {
            String githubPath = br.OutputRepoURL;
            String rubyVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-ruby-hostingstart-{0}-task", rubyVersionDash);
            String appName = br.TestWebAppName;
            String imageName = br.OutputImage;
            String planName = "appsvcbuild-ruby-hostingstart-plan";

            LogInfo("creating acr task for ruby hostingstart" + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            LogInfo("done creating acr task for ruby hostingstart" + br.Version);

            LogInfo("creating webapp for ruby hostingstart " + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, acrPassword, appName, imageName, planName);
            LogInfo("done creating webapp for ruby hostingstart " + br.Version);
            return;
        }

        private static async System.Threading.Tasks.Task PushGithubBaseAsync(BuildRequest br)
        {
            String outputRepoName = String.Format("rubybase-{0}", br.Version);

            LogInfo("creating github files for ruby base " + br.Version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, outputRepoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, "master");
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(outputRepoName))
            {
                _githubUtils.Clone(
                     String.Format("https://github.com/blessedimagepipeline/rubybase-{0}.git", br.Version),
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
                String.Format("{0}\\{1}\\base_images", localTemplateRepoPath, br.TemplateName),
                localOutputRepoPath,
                false);
            _githubUtils.FillTemplate(
                String.Format("{0}\\DockerFile", localOutputRepoPath),
                new List<String> { String.Format("ENV RUBY_VERSION=\"{0}\"", br.Version) },
                new List<int> { 4 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, String.Format("[appsvcbuild] Add ruby {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for ruby base " + br.Version);
            return;
        }

        private static async System.Threading.Tasks.Task PushGithubHostingStartAsync(BuildRequest br)
        {
            LogInfo("creating github files for ruby " + br.Version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.OutputRepoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, br.Branch);
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
                String.Format("{0}\\{1}\\main_images", localTemplateRepoPath, br.TemplateName),
                localOutputRepoPath,
                false);
            _githubUtils.FillTemplate(
                String.Format("{0}\\DockerFile", localOutputRepoPath),
                new List<String> { String.Format("FROM appsvcbuildacr.azurecr.io/rubybase:{0}", br.Version),
                                   String.Format("RUN export RUBY_VERSION=\"{0}\"", br.Version)},
                new List<int> { 1, 4 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, String.Format("[appsvcbuild] Add ruby {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for ruby " + br.Version);
            return;
        }
    }
}

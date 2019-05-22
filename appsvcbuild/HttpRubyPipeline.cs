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
            List<BuildRequest> buildRequests = JsonConvert.DeserializeObject<List<BuildRequest>>(requestBody);
            foreach (BuildRequest br in buildRequests)
            {
                br.processAddDefaults();
            }

            if (buildRequests == null)
            {
                LogInfo("Failed: missing parameters `newTags` in body");
                //await _mailUtils.SendFailureMail("Failed: missing parameters `newTags` in body", GetLog());
                return new BadRequestObjectResult("Failed: missing parameters `newTags` in body");
            }
            else if (buildRequests.Count == 0)
            {
                LogInfo("no new ruby tags found");
                //await _mailUtils.SendSuccessMail(new List<string> { "fix me later" }, GetLog());
                return (ActionResult)new OkObjectResult($"no new ruby tags found");
            }
            else
            {
                try
                {
                    _mailUtils._buildRequest = buildRequests[0];
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
            String rubyVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-ruby-base-{0}-task", rubyVersionDash);            

            LogInfo("creating acr task for ruby base " + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.RubyBaseOutputRepoURL, _secretsUtils._gitToken, br.RubyBaseOutputImageName);
            LogInfo("done creating acr task for ruby base " + br.Version);
            return;
        }

        public static async System.Threading.Tasks.Task CreateRubyHostingStartPipeline(BuildRequest br)
        {
            String rubyVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-ruby-hostingstart-{0}-task", rubyVersionDash);
            String planName = "appsvcbuild-ruby-hostingstart-plan";

            LogInfo("creating acr task for ruby hostingstart" + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.OutputRepoURL, _secretsUtils._gitToken, br.OutputImageName);
            LogInfo("done creating acr task for ruby hostingstart" + br.Version);

            LogInfo("creating webapp for ruby hostingstart " + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, acrPassword, br.WebAppName, br.OutputImageName, planName);
            LogInfo("done creating webapp for ruby hostingstart " + br.Version);
            return;
        }

        private static async System.Threading.Tasks.Task PushGithubBaseAsync(BuildRequest br)
        {
            LogInfo("creating github files for ruby base " + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String parent = String.Format("F:\\home\\site\\wwwroot\\appsvcbuild{0}", timeStamp);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.RubyBaseOutputRepoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, br.TemplateRepoBranchName);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(br.RubyBaseOutputRepoOrgName, br.RubyBaseOutputRepoName))
            {
                _githubUtils.Clone(
                     br.RubyBaseOutputRepoURL,
                     localOutputRepoPath,
                     br.RubyBaseOutputRepoBranchName);
            }
            else
            {
                await _githubUtils.InitGithubAsync(br.RubyBaseOutputRepoOrgName, br.RubyBaseOutputRepoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, br.RubyBaseOutputRepoOrgName, br.RubyBaseOutputRepoName);
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
            _githubUtils.CommitAndPush(localOutputRepoPath, br.RubyBaseOutputRepoBranchName, String.Format("[appsvcbuild] Add ruby {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for ruby base " + br.Version);
            return;
        }

        private static async System.Threading.Tasks.Task PushGithubHostingStartAsync(BuildRequest br)
        {
            LogInfo("creating github files for ruby " + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String parent = String.Format("F:\\home\\site\\wwwroot\\appsvcbuild{0}", timeStamp);
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
                String.Format("{0}\\{1}\\main_images", localTemplateRepoPath, br.TemplateName),
                localOutputRepoPath,
                false);
            _githubUtils.FillTemplate(
                String.Format("{0}\\DockerFile", localOutputRepoPath),
                new List<String> { String.Format("FROM appsvcbuildacr.azurecr.io/{0}", br.RubyBaseOutputImageName),
                                   String.Format("RUN export RUBY_VERSION=\"{0}\"", br.Version)},
                new List<int> { 1, 4 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.OutputRepoBranchName, String.Format("[appsvcbuild] Add ruby {0}", br.Version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for ruby " + br.Version);
            return;
        }
    }
}

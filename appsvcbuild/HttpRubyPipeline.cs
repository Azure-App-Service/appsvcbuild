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
        private static String _githubURL = "https://github.com/Azure-App-Service/ruby-template.git";
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
            List<String> newTags = data?.newTags.ToObject<List<String>>();

            if (newTags == null)
            {
                LogInfo("Failed: missing parameters `newTags` in body");
                await _mailUtils.SendFailureMail("Failed: missing parameters `newTags` in body", GetLog());
                return new BadRequestObjectResult("Failed: missing parameters `newTags` in body");
            }
            else if (newTags.Count == 0)
            {
                LogInfo("no new ruby tags found");
                await _mailUtils.SendSuccessMail(newTags, GetLog());
                return (ActionResult)new OkObjectResult($"no new ruby tags found");
            }
            else
            {
                try
                {
                    LogInfo($"HttpRubyPipeline executed at: { DateTime.Now }");
                    LogInfo(String.Format("new ruby tags found {0}", String.Join(", ", newTags)));
                
                    List<String> newVersions = await MakePipeline(newTags, log);
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

        public static async Task<List<string>> MakePipeline(List<String> newTags, ILogger log)
        {
            List<String> newVersions = new List<String>();

            foreach (String t in newTags)
            {
                String version = t.Split(':')[1];
                newVersions.Add(version);
                int tries = 3;
                while (true)
                {
                    try
                    {
                        tries--;
                        _mailUtils._version = version;
                        LogInfo("creating pipeling for ruby " + version);
                        await PushGithubBaseAsync(t, version);
                        await CreateRubyBasePipeline(version);
                        await PushGithubHostingStartAsync(t, version);
                        await CreateRubyHostingStartPipeline(version); LogInfo(String.Format("ruby {0} built", version));
                        LogInfo("done creating pipeling for ruby " + version);
                        break;
                    }
                    catch (Exception e)
                    {
                        LogInfo(e.ToString());
                        if (tries <= 0)
                        {
                            LogInfo(String.Format("ruby {0} failed", version));
                            throw e;
                        }
                        LogInfo("trying again");
                    }
                }
            }
            return newVersions;
        }

        public static async System.Threading.Tasks.Task CreateRubyBasePipeline(String version)
        {
            String githubPath = String.Format("https://github.com/blessedimagepipeline/rubybase-{0}", version);
            String rubyVersionDash = version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-ruby-base-{0}-task", rubyVersionDash);
            String imageName = String.Format("rubybase:{0}", version);
            

            LogInfo("creating acr task for ruby base " + version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            LogInfo("done creating acr task for ruby base " + version);
            return;
        }

        public static async System.Threading.Tasks.Task CreateRubyHostingStartPipeline(String version)
        {
            String githubPath = String.Format("https://github.com/blessedimagepipeline/ruby-{0}", version);
            String rubyVersionDash = version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-ruby-hostingstart-{0}-task", rubyVersionDash);
            String appName = String.Format("appsvcbuild-ruby-hostingstart-{0}-site", rubyVersionDash);
            String webhookName = String.Format("appsvcbuildrubyhostingstart{0}wh", version.Replace(".", ""));
            String imageName = String.Format("ruby:{0}", version);
            String planName = "appsvcbuild-ruby-hostingstart-plan";

            LogInfo("creating acr task for ruby hostingstart" + version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, githubPath, _secretsUtils._gitToken, imageName);
            LogInfo("done creating acr task for ruby hostingstart" + version);
            LogInfo("creating webapp for ruby hostingstart " + version);
            String cdUrl = _pipelineUtils.CreateWebapp(version, acrPassword, appName, imageName, planName);
            LogInfo("done creating webapp for ruby hostingstart " + version);
            return;
        }

        private static String getTemplate(String version)
        {
            return "templates";
        }

        private static async System.Threading.Tasks.Task PushGithubBaseAsync(String tag, String version)
        {
            String repoName = String.Format("rubybase-{0}", version);

            LogInfo("creating github files for ruby base " + version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);

            String templateRepo = String.Format("{0}\\ruby-template", parent);
            String rubyRepo = String.Format("{0}\\{1}", parent, repoName);

            _githubUtils.Clone(_githubURL, templateRepo);
            _githubUtils.FillTemplate(
                templateRepo,
                String.Format("{0}\\{1}\\base_images", templateRepo, getTemplate(version)),
                String.Format("{0}\\{1}", templateRepo, repoName),
                String.Format("{0}\\{1}\\DockerFile", templateRepo, repoName),
                new List<String> { String.Format("ENV RUBY_VERSION=\"{0}\"", version) },
                new List<int> { 4 },
                false);

            _githubUtils.CreateDir(rubyRepo);
            if (await _githubUtils.RepoExistsAsync(repoName))
            {
                _githubUtils.Clone(
                    String.Format("https://github.com/blessedimagepipeline/{0}.git", repoName),
                    rubyRepo);
            }
            else
            {
                await _githubUtils.InitGithubAsync(repoName);
                _githubUtils.Init(rubyRepo);
                _githubUtils.AddRemote(rubyRepo, repoName);
            }
            
            _githubUtils.DeepCopy(String.Format("{0}\\{1}", templateRepo, repoName), rubyRepo);
            _githubUtils.Stage(rubyRepo, "*");
            _githubUtils.CommitAndPush(rubyRepo, String.Format("[appsvcbuild] Add ruby {0}", version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for ruby base " + version);
            return;
        }

        private static async System.Threading.Tasks.Task PushGithubHostingStartAsync(String tag, String version)
        {
            String repoName = String.Format("ruby-{0}", version);

            LogInfo("creating github files for ruby " + version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);

            String templateRepo = String.Format("{0}\\ruby-template", parent);
            String rubyRepo = String.Format("{0}\\{1}", parent, repoName);

            _githubUtils.Clone(_githubURL, templateRepo);
            _githubUtils.FillTemplate(
                templateRepo,
                String.Format("{0}\\{1}\\main_images", templateRepo, getTemplate(version)),
                String.Format("{0}\\{1}", templateRepo, repoName),
                String.Format("{0}\\{1}\\DockerFile", templateRepo, repoName),
                new List<String> { String.Format("FROM appsvcbuildacr.azurecr.io/rubybase:{0}", version),
                                   String.Format("RUN export RUBY_VERSION=\"{0}\"", version)},
                new List<int> { 1, 4 },
                false);

            _githubUtils.CreateDir(rubyRepo);
            if (await _githubUtils.RepoExistsAsync(repoName))
            {
                _githubUtils.Clone(
                    String.Format("https://github.com/blessedimagepipeline/{0}.git", repoName),
                    rubyRepo);
            }
            else
            {
                await _githubUtils.InitGithubAsync(repoName);
                _githubUtils.Init(rubyRepo);
                _githubUtils.AddRemote(rubyRepo, repoName);
            }

            _githubUtils.DeepCopy(String.Format("{0}\\{1}", templateRepo, repoName), rubyRepo);
            _githubUtils.Stage(rubyRepo, "*");
            _githubUtils.CommitAndPush(rubyRepo, String.Format("[appsvcbuild] Add ruby {0}", version));
            //_githubUtils.CleanUp(parent);
            LogInfo("done creating github files for ruby " + version);
            return;
        }
    }
}

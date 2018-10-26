using System;
using System.Text;
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

namespace appsvcbuild
{
    public static class PhpBuildPipeline
    {
        private static TraceWriter _log;
        private static String _githubURL = "https://github.com/patricklee2/php-ci.git";

        private static SecretsUtils _secretsUtils;
        private static MailUtils _mailUtils;
        private static DockerhubUtils _dockerhubUtils;
        private static GitHubUtils _githubUtils;
        private static PipelineUtils _pipelineUtils;

        // run 8am utc, 12am pst
        [FunctionName("PhpBuildPipeline")]
        public static async System.Threading.Tasks.Task Run([TimerTrigger("0 8 * * *")]TimerInfo myTimer, TraceWriter log)
        {
            StringBuilder emailLog = new StringBuilder();
            try
            {
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

                _log.Info($"php appsvcbuild executed at: { DateTime.Now }");
                emailLog.AppendLine($"php appsvcbuild executed at: { DateTime.Now }");

                List<String> newTags = await _dockerhubUtils.PollDockerhub("https://registry.hub.docker.com/v2/repositories/library/php/tags",
                    new Regex("^[0-9]+\\.[0-9]+\\.[0-9]+-apache$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                    DateTime.Now.AddDays(-1));

                _log.Info(String.Format("new php tags found {0}", String.Join(", ", newTags)));
                emailLog.AppendLine(String.Format("new php tags found {0}", String.Join(", ", newTags)));

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
                            _log.Info(String.Format("php {0} built", version));
                            emailLog.AppendLine(String.Format("php {0} built", version));
                            break;
                        }
                        catch (Exception e)
                        {
                            _log.Info(e.ToString());
                            emailLog.AppendLine(e.ToString());
                            if (tries <= 0)
                            {
                                _log.Info(String.Format("php {0} failed", version));
                                emailLog.AppendLine(String.Format("php {0} failed", version));
                                throw e;
                            }
                            _log.Info("trying again");
                            emailLog.AppendLine("trying again");
                        }
                    }
                }
                await _mailUtils.SendSuccessMail(newVersions, emailLog.ToString());
            }
            catch (Exception e)
            {
                await _mailUtils.SendFailureMail(e.ToString(), emailLog.ToString());
            }
        }

        public static void CreatePhpPipeline(String version)
        {
            CreatePhpHostingStartPipeline(version);
            CreatePhpAppPipeline(version);
        }

        public static void CreatePhpHostingStartPipeline(String version)
        {
            _log.Info("creating pipeling for php hostingstart " + version);

            String phpVersionDash = version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-hostingstart-{0}-task", phpVersionDash);
            String appName = String.Format("appsvcbuild-php-hostingstart-{0}-site", phpVersionDash);
            String webhookName = String.Format("appsvcbuildphphostingstart{0}wh", version.Replace(".", ""));
            String gitPath = String.Format("{0}#master:{1}-apache", _githubURL, version);
            String imageName = String.Format("php:{0}-apache", version);

            _log.Info("creating acr task for php hostingstart " + version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, gitPath, _secretsUtils._gitToken, imageName);
            _log.Info("creating webapp for php hostingstart " + version);
            String cdUrl = _pipelineUtils.CreateWebapp(version, acrPassword, appName, imageName);
            _pipelineUtils.CreateWebhook(cdUrl, webhookName, imageName);
        }

        public static void CreatePhpAppPipeline(String version)
        {
            _log.Info("creating pipeling for php app " + version);

            String phpVersionDash = version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-php-app-{0}-task", phpVersionDash);
            String appName = String.Format("appsvcbuild-php-app-{0}-site", phpVersionDash);
            String webhookName = String.Format("appsvcbuildphpapp{0}wh", version.Replace(".", ""));
            String gitPath = String.Format("{0}#master:{1}-app-apache", _githubURL, version);
            String imageName = String.Format("phpapp:{0}-apache", version);

            _log.Info("creating acr task for php app" + version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, gitPath, _secretsUtils._gitToken, imageName);
            _log.Info("creating webapp for php app" + version);
            String cdUrl = _pipelineUtils.CreateWebapp(version, acrPassword, appName, imageName);
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
            _log.Info("creating github files for php " + version);
            Random random = new Random();
            String i = random.Next(0, 9999).ToString(); // dont know how to delete files in functions, probably need a file/blob share
            String parent = String.Format("D:\\home\\site\\wwwroot\\appsvcbuild{0}", i);
            _githubUtils.CreateDir(parent);
            String localRepo = String.Format("{0}\\php-ci", parent);

            _githubUtils.Clone(_githubURL, localRepo);

            _githubUtils.FillTemplate(
                localRepo,
                String.Format("{0}\\{1}", localRepo, getTemplate(version)),
                String.Format("{0}\\{1}-apache", localRepo, version),
                String.Format("{0}\\{1}-apache\\DockerFile", localRepo, version),
                String.Format("FROM php:{0}-apache\n", version),
                false);
            _githubUtils.FillTemplate(
                localRepo,
                String.Format("{0}\\template-app-apache", localRepo),
                String.Format("{0}\\{1}-app-apache", localRepo, version),
                String.Format("{0}\\{1}-app-apache\\DockerFile", localRepo, version),
                String.Format("FROM appsvcbuildacr.azurecr.io/php:{0}-apache\n", version),
                false);
            _githubUtils.Stage(localRepo, String.Format("{0}\\{1}-apache", localRepo, version));
            _githubUtils.Stage(localRepo, String.Format("{0}\\{1}-app-apache", localRepo, version));
            _githubUtils.CommitAndPush(_githubURL, localRepo, String.Format("[appsvcbuild] Add php {0}", version));
            //_githubUtils.CleanUp(parent);
        }
    }
}

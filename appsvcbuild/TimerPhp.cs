using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
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
using System.Threading;
using Microsoft.ApplicationInsights;

namespace appsvcbuild
{
    public static class TimerPhp
    {
        [FunctionName("TimerPhp")]
        public static async System.Threading.Tasks.Task RunAsync([TimerTrigger("0 8 * * *")]TimerInfo myTimer, ILogger log)
        {
            TelemetryClient telemetry = new TelemetryClient();
            telemetry.TrackEvent("TimerPhp started");

            try {
                SecretsUtils secretsUtils = new SecretsUtils();
                await secretsUtils.GetSecrets();
                DockerhubUtils dockerhubUtils = new DockerhubUtils();

                List<String> newTags = await dockerhubUtils.PollDockerhub("https://registry.hub.docker.com/v2/repositories/library/php/tags",
                    new Regex("^[0-9]+\\.[0-9]+\\.[0-9]+-apache$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                    DateTime.Now.AddDays(-1));

                log.LogInformation(String.Format("Php: {0} tags found {1}", newTags.Count, String.Join(", ", newTags)));
                telemetry.TrackEvent(String.Format("Php: {0} tags found {1}", newTags.Count, String.Join(", ", newTags)));

                HttpClient client = new HttpClient();
                String url = String.Format("https://appsvcbuildfunc.azurewebsites.net/api/HttpPhpPipeline?code={0}", secretsUtils._appsvcbuildfuncMaster);
                String body = "{\"newTags\": " + JsonConvert.SerializeObject(newTags) + "}";

                HttpResponseMessage response = await client.PostAsync(url, new StringContent(body));
                String result = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    log.LogInformation(result);
                    telemetry.TrackEvent(result);
                }
                else
                {
                    log.LogInformation(response.ToString());
                    telemetry.TrackEvent(response.ToString());
                }
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.ToString());
                telemetry.TrackException(ex);
            }
        }
    }
}

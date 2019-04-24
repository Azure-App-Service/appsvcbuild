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
    public static class TimerRuby
    {
        [FunctionName("TimerRuby")]
        public static async System.Threading.Tasks.Task RunAsync([TimerTrigger("0 8 * * *")]TimerInfo myTimer, ILogger log)
        {
            TelemetryClient telemetry = new TelemetryClient();
            telemetry.TrackEvent("TimerRuby started");

            try
            {
                SecretsUtils secretsUtils = new SecretsUtils();
                await secretsUtils.GetSecrets();
                DockerhubUtils dockerhubUtils = new DockerhubUtils();

                List<String> newTags = new List<string> { "ruby:2.3.8", "ruby:2.4.5", "ruby:2.5.5", "ruby:2.6.2" };

                log.LogInformation(String.Format("Ruby: {0} tags found {1}", newTags.Count, String.Join(", ", newTags)));
                telemetry.TrackEvent(String.Format("Ruby: {0} tags found {1}", newTags.Count, String.Join(", ", newTags)));
                foreach (String t in newTags)
                {
                    try
                    {
                        List<String> tag = new List<String> { t };
                        HttpClient client = new HttpClient();
                        String url = String.Format("https://appsvcbuildfunc.azurewebsites.net/api/HttpRubyPipeline?code={0}", secretsUtils._appsvcbuildfuncMaster);
                        String body = "{\"newTags\": " + JsonConvert.SerializeObject(tag) + "}";
                        client.Timeout = new TimeSpan(3, 0, 0);

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
            catch (Exception ex)
            {
                log.LogInformation(ex.ToString());
                telemetry.TrackException(ex);
            }
        }
    }
}

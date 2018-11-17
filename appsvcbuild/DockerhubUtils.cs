using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace appsvcbuild
{
    public class DockerhubUtils
    {
        public ILogger _log { get; set; }

        public DockerhubUtils() { }

        public async Task<List<String>> PollDockerhub(String dockerHubURL, Regex regex, DateTime cutOff)
        {
            try
            {
                //_log.Info("polling dockerhub");
                //_log.Info("cutoff " + cutOff.ToString());

                List<String> newTags = new List<String>();

                String nextUrl = dockerHubURL;
                List<Tag> tags = null;
                while (nextUrl != null)
                {
                    //_log.Info("next page " + nextUrl);
                    (nextUrl, tags) = await GetPage(nextUrl, cutOff);
                    foreach (Tag t in tags)
                    {
                        if (regex.IsMatch(t.Name))
                        {
                            DateTime lastUpdated = Convert.ToDateTime(t.LastUpdated);
                            if (lastUpdated >= cutOff)
                            {
                                //_log.Info(t.Name);
                                newTags.Add(t.Name);
                            }
                        }
                    }
                }
                return newTags;
            }
            catch (Exception ex)
            {
                //_log.Info($"Exception found {ex.Message}");
                return new List<String>();
            }
        }

        public async Task<Tuple<String, List<Tag>>> GetPage(String url, DateTime cutoff)
        {
            HttpClient httpClient = new HttpClient();
            try
            {
                HttpResponseMessage response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                string contentString = await response.Content.ReadAsStringAsync();
                TagList tagList = JsonConvert.DeserializeObject<TagList>(contentString);
                return Tuple.Create<String, List<Tag>>(tagList.Next, tagList.Results);
            }
            catch (Exception ex)
            {
                //_log.Info($"Exception found {ex.Message}");
                return Tuple.Create<String, List<Tag>>(null, null);
            }
            finally
            {
                httpClient.Dispose();
            }
        }
    }
}

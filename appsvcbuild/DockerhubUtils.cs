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

                List<String> newResults = new List<String>();

                String nextUrl = dockerHubURL;
                List<Result> results = null;
                while (nextUrl != null)
                {
                    //_log.Info("next page " + nextUrl);
                    (nextUrl, results) = await GetPage(nextUrl, cutOff);
                    foreach (Result r in results)
                    {
                        DateTime lastUpdated = Convert.ToDateTime(r.LastUpdated);
                        if (regex.IsMatch(r.Name) && lastUpdated >= cutOff)
                        {
                            //_log.Info(t.Name);
                            newResults.Add(r.Name);
                        }
                    }
                }
                return newResults;
            }
            catch (Exception ex)
            {
                //_log.Info($"Exception found {ex.Message}");
                return new List<String>();
            }
        }

        public async Task<List<String>> PollDockerhubRepos(String dockerHubURL, Regex repoRegex, Regex tagRegex, DateTime cutOff)
        {
            List<String> newTags = new List<String>();
            List<String> repos = await PollDockerhub(dockerHubURL, repoRegex, cutOff);
            foreach(String repo in repos)
            {
                List<String> tags = await PollDockerhub(
                    String.Format("https://registry.hub.docker.com/v2/repositories/oryxprod/{0}/tags", repo),
                    tagRegex,
                    cutOff);
                foreach (String t in tags)
                {
                    newTags.Add(String.Format("{0}:{1}", repo, t));
                }
            }
            return newTags;
        }

        public async Task<Tuple<String, List<Result>>> GetPage(String url, DateTime cutoff)
        {
            HttpClient httpClient = new HttpClient();
            try
            {
                HttpResponseMessage response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                string contentString = await response.Content.ReadAsStringAsync();
                ResultList resultList = JsonConvert.DeserializeObject<ResultList>(contentString);
                return Tuple.Create<String, List<Result>>(resultList.Next, resultList.Results);
            }
            catch (Exception ex)
            {
                //_log.Info($"Exception found {ex.Message}");
                return Tuple.Create<String, List<Result>>(null, null);
            }
            finally
            {
                httpClient.Dispose();
            }
        }
    }
}

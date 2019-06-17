using appsvcbuild;
using appsvcbuildPR;
using Microsoft.Azure.Management.ContainerRegistry;
using Microsoft.Azure.Management.WebSites;
using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace appsvcbuildCleanup
{
    class Program
    {
        static Boolean isTemp(Repo r)
        {
            return r.name.Contains("2019");
        }

        static void Main(string[] args)
        {
            cleanupGithub();
            cleanupACR();
            // cleanupWebapp();  do via portal
            while (true)    //sleep until user exits
            {
                System.Threading.Thread.Sleep(5000);
            }
        }

        private static async void cleanupACR()
        {
            cleanupACRStack("node");
            cleanupACRStack("nodeapp");
            cleanupACRStack("php");
            cleanupACRStack("php-xdebug");
            cleanupACRStack("phpxdebug");
            cleanupACRStack("phpapp");
            cleanupACRStack("python");
            cleanupACRStack("pythonapp");
            cleanupACRStack("dotnetcore");
            cleanupACRStack("ruby");
            cleanupACRStack("kudu");
        }

        private static async void cleanupACRStack(String stack)
        {
            SecretsUtils secretsUtils = new SecretsUtils();
            await secretsUtils.GetSecrets();
            PipelineUtils pipelineUtils = new PipelineUtils(
                new ContainerRegistryManagementClient(secretsUtils._credentials),
                new WebSiteManagementClient(secretsUtils._credentials),
                secretsUtils._subId);
            var client = new RestClient(String.Format("https://appsvcbuildacr.azurecr.io/v2/{0}/tags/list", stack));
            var request = new RestRequest(Method.GET);
            request.AddHeader("Host", "appsvcbuildacr.azurecr.io");
                request.AddHeader("Cache-Control", "no-cache");
                String token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    String.Format("{0}:{1}", "appsvcbuildacr", secretsUtils._acrPassword)));
            request.AddHeader("Authorization", String.Format("Basic {0}", token));

            // add logic for next page // or just run again
            IRestResponse response = client.Execute(request);
            var json = JsonConvert.DeserializeObject<dynamic>(response.Content.ToString());
            var tags = json.tags;

            foreach(String t in tags)
            {
                if (t.Contains("2019"))
                {
                    Console.WriteLine(String.Format("{0}:{1}", stack, t));
                    pipelineUtils.DeleteImage("appsvcbuildacr", stack, t, "appsvcbuildacr", secretsUtils._acrPassword);
                }
                
            }
        }
        private static async void cleanupGithub()
        {
            String _gitToken = File.ReadAllText("../../../gitToken.txt");

            // list temp repos
            HttpClient httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("patricklee2");
            HttpResponseMessage response = null;
            List<Repo> resultList = new List<Repo>();
            List<Repo> stackRepos = null;
            int run = 0;
            while (true)
            {
                String generatedReposURL = String.Format("https://api.github.com/orgs/{0}/repos?page={1}&per_page=30&sort=full_name&direction=asc", "blessedimagepipeline", run);
                response = await httpClient.GetAsync(generatedReposURL);
                response.EnsureSuccessStatusCode();
                string contentString = await response.Content.ReadAsStringAsync();
                List<Repo> l = JsonConvert.DeserializeObject<List<Repo>>(contentString);
                resultList.AddRange(l);
                run++;
                if (l.Count < 30)
                {
                    break;
                }
            }

            stackRepos = resultList.FindAll(isTemp);
            GitHubUtils gitHubUtils = new GitHubUtils(_gitToken);

            foreach (Repo r in stackRepos)
            {
                Console.WriteLine(r.full_name);
                gitHubUtils.DeleteGithubAsync("blessedimagepipeline", r.name);
                // delete image

                //delete webapp
            }
        }
    }
}

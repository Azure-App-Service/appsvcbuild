using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RestSharp;
using appsvcbuild;

namespace appsvcbuildconsole
{
    class Program
    {
        static void Main(string[] args)
        {
            buildAsync();
            while (true)    //sleep until user exits
            {
                System.Threading.Thread.Sleep(5000);
            }
        }
        static async void buildAsync()
        {
            String text = "";
            List<BuildRequest> buildRequests = new List<BuildRequest>();

            //text = getStack("kudu", "dev");
            //buildRequests.AddRange(JsonConvert.DeserializeObject<List<BuildRequest>>(text));

            //text = getStack("dotnetcore", "dev");
            //buildRequests.AddRange(JsonConvert.DeserializeObject<List<BuildRequest>>(text));

            //text = getStack("node", "dev");
            //buildRequests.AddRange(JsonConvert.DeserializeObject<List<BuildRequest>>(text));

            //text = getStack("php", "dev");
            //buildRequests.AddRange(JsonConvert.DeserializeObject<List<BuildRequest>>(text));

            //text = getStack("python", "dev");
            //buildRequests.AddRange(JsonConvert.DeserializeObject<List<BuildRequest>>(text));

            //text = getStack("ruby", "dev");
            //buildRequests.AddRange(JsonConvert.DeserializeObject<List<BuildRequest>>(text));

            text = File.ReadAllText("../../../requests.json");
            buildRequests.AddRange(JsonConvert.DeserializeObject<List<BuildRequest>>(text));
            
            foreach (BuildRequest br in buildRequests)
            {
                Task.Run(() => makeRequestAsync(br));
                System.Threading.Thread.Sleep(1 * 60 * 1000); // sleep 1 mins between builds
            }
        }

        static String getStack(String stack, String branchName)
        {
            return getConfig($"https://raw.githubusercontent.com/Azure-App-Service/{stack}-template/{branchName}/blessedImageConfig-temp.json");
        }

        static String getConfig(String gitURL)
        {
            //Console.WriteLine($"getting config {gitURL}");

            var client = new RestClient(gitURL);
            client.Timeout = 1000 * 60; // 1min
            var request = new RestRequest(Method.GET);
            request.AddHeader("cache-control", "no-cache");
            
            IRestResponse response = client.Execute(request);
            //Console.WriteLine(response.StatusCode.ToString());
            if (!response.StatusCode.ToString().ToLower().Contains("ok"))
            {
                return "";
            }
            //Console.WriteLine(response.Content.ToString());

            return response.Content.ToString();
        }

        static async Task makeRequestAsync(BuildRequest br)
        {
            Console.WriteLine(String.Format("making tag: {0} {1}", br.Stack, br.Version));
            String stack = String.Format("{0}{1}", br.Stack.ToUpper()[0], br.Stack.ToLower().Substring(1));
            String secretKey = File.ReadAllText("../../../secret.txt");
            //String url = String.Format("https://appsvcbuildfunc.azurewebsites.net/api/Http{0}Pipeline?code={1}", stack, secretKey);
            //String url = String.Format("http://localhost:7071/api/Http{0}Pipeline", stack);
            //String url = String.Format("https://appsvcbuildfunc-test.azurewebsites.net/api/HttpBuildPipeline_HttpStart?code={0}", secretKey);
            String url = "http://localhost:7071/api/HttpBuildPipeline_HttpStart";

            String body = JsonConvert.SerializeObject(br);
            var client = new RestClient(url);
            client.Timeout = 1000 *60; // 1min
            var request = new RestRequest(Method.POST);
            request.AddHeader("cache-control", "no-cache");
            request.AddHeader("Content-Type", "application/json");
            request.AddParameter("undefined", body, ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);
            Console.WriteLine(response.StatusCode.ToString());
            if (!response.StatusCode.ToString().ToLower().Contains("accepted"))
            {
                return;
            }
            //Console.WriteLine(response.Content.ToString());

            var result = JsonConvert.DeserializeObject<dynamic>(response.Content.ToString());
            String statusURL = result.statusQueryGetUri;
            Console.WriteLine(String.Format("{0} {1} statusQueryGetUri {2}", br.Stack, br.Version, statusURL));
            while (true)
            {
                client = new RestClient(statusURL);
                client.Timeout = 1000 * 60; // 1min
                request = new RestRequest(Method.GET);
                request.AddHeader("cache-control", "no-cache");
                request.AddHeader("Content-Type", "application/json");
                request.AddParameter("undefined", body, ParameterType.RequestBody);
                response = client.Execute(request);
                //Console.WriteLine(response.StatusCode.ToString());
                //Console.WriteLine(response.Content.ToString());
                result = JsonConvert.DeserializeObject<dynamic>(response.Content.ToString());
                String status = result.runtimeStatus;
                if (!status.ToLower().Equals("running"))
                {
                    Console.WriteLine(String.Format("{0} {1} completed", br.Stack, br.Version));
                    try
                    {
                        if (result.output.ToString().ToLower().Contains("success"))
                        {
                            Console.WriteLine(String.Format("{0} {1} success", br.Stack, br.Version));
                        }
                        else
                        {
                            Console.WriteLine(String.Format("{0} {1} failure", br.Stack, br.Version));
                            Console.WriteLine(result.output.ToString());
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(String.Format("{0} {1} failure", br.Stack, br.Version));
                        Console.WriteLine(e.ToString());
                    }
                    break;
                }
                else
                {
                    System.Threading.Thread.Sleep(1 * 60 * 1000); // sleep 1 mins between builds
                }
            }
        }
    }
}

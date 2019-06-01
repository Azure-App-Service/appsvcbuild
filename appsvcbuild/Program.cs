using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using RestSharp;

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
            string text = File.ReadAllText("requests.json");

            foreach(String t in tags)
            {
                await makeRequestAsync(t);
                System.Threading.Thread.Sleep(1 * 60 * 1000); // sleep 1 mins between builds
            }
        }

        static async Task makeRequestAsync(String tag)
        {
            Console.WriteLine(String.Format("making tag: {0}", tag));
            String stack = "";
            if (tag.ToLower().Contains("python"))
            {
                stack = "Python";
            } else if (tag.ToLower().Contains("node"))
            {
                stack = "Node";
            }
            else if (tag.ToLower().Contains("ruby"))
            {
                stack = "Ruby";
            } else if (tag.ToLower().Contains("php"))
            {
                stack = "Php";
            } else if (tag.ToLower().Contains("dotnetcore"))
            {
                stack = "Dotnetcore";
            }
            String secretKey = "1xLSjbv/Y/UzqTz8efQO7xEV2an9k28zzkLqafcm4QnXygnnrizuHA==";
            //String url = String.Format("https://appsvcbuildfunc.azurewebsites.net/api/Http{0}Pipeline?code={1}", stack, secretKey);
            String url = String.Format("http://localhost:7071/api/Http{0}Pipeline", stack);

            String body = String.Format("{{\"newTags\": [\"{0}\"]}}", tag);
            var client = new RestClient(url);
            client.Timeout = 1000 * 60 * 60; //1h
            var request = new RestRequest(Method.POST);
            request.AddHeader("cache-control", "no-cache");
            request.AddHeader("Content-Type", "application/json");
            request.AddParameter("undefined", body, ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);
            Console.WriteLine(response.StatusCode.ToString());
            
            String result = response.Content.ToString();
            Console.WriteLine(result);
        }
    }
}


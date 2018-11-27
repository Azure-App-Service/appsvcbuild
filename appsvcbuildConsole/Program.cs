using System;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Threading.Tasks;

// dotnet publish -c Release -r win10-x64
namespace appsvcbuildConsole
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                String action;
                List<String> newTags = new List<String>();
                String stack;
                if (args.Length == 3)
                {
                    action = args[0];
                    action = action.ToLower().Trim();
                    stack = args[1];
                    newTags.Add(args[2]);
                }
                else
                {
                    Console.Write("requires 3 arguments, action, stack and tag");
                    return;
                }

                if (action == "add")
                {
                    add(stack, newTags);
                }
                else if (action == "delete")
                {
                    delete(stack, newTags);
                }
                else
                {
                    Console.WriteLine("unsupported action, only supported actions are 'add' and 'delete'");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

        }

        private static void add(String stack,  List<String> newTags)
        {
            String appsvcbuildfuncMaster = GetAppSetting("appsvcbuildfuncMaster");

            String url;
            if (stack.ToLower().Trim() == "node")
            {
                url = String.Format("https://appsvcbuildfunc.azurewebsites.net/api/HttpNodePipeline?code={0}", appsvcbuildfuncMaster);
            }
            else if (stack.ToLower().Trim() == "php")
            {
                url = String.Format("https://appsvcbuildfunc.azurewebsites.net/api/HttpPhpPipeline?code={0}", appsvcbuildfuncMaster);
            }
            else
            {
                Console.WriteLine("unsupported stack, only php and node are supported");
                return;
            }

            Task t = SendRequestAsync(newTags, url);
            t.Wait();
        }

        private static void delete(String stack, List<String> tags)
        {
            Console.WriteLine("delete currently not supported");
            return;
        }

        private static async System.Threading.Tasks.Task SendRequestAsync(List<String> newTags, String url)
        {
            try
            {
                HttpClient client = new HttpClient();

                String body = "{\"newTags\": " + JsonConvert.SerializeObject(newTags) + "}";
                client.Timeout = TimeSpan.FromHours(3);
                Console.WriteLine("waiting...");
                HttpResponseMessage response = await client.PostAsync(url, new StringContent(body));
                String result = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == HttpStatusCode.OK) //should be accepted
                {
                    Console.WriteLine(result);
                }
                else
                {
                    //times out after 5 mins but request was accepted
                    Console.WriteLine(response.ToString());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private static String GetAppSetting(string name)
        {
            String value = System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
            if (value == "")
            {
                Console.WriteLine(String.Format("missing env variable {0}", name));
                throw new Exception(String.Format("missing env variable {0}", name));
            }
            return value;
        }
    }
}

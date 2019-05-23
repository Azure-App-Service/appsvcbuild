using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace appsvcbuild
{
    public static class HttpBuildPipeline
    {
        [FunctionName("HttpBuildPipeline")]
        public static async Task<string> RunOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContext context)
        {
            String content = context.GetInput<String>();
            BuildRequest buildRequest = JsonConvert.DeserializeObject<BuildRequest>(content);
            buildRequest.processAddDefaults();

            return await context.CallActivityAsync<string>("HttpBuildPipeline_Hello", buildRequest);

        }

        [FunctionName("HttpBuildPipeline_Hello")]
        public static async Task<String> SayHello([ActivityTrigger] BuildRequest br, ILogger log)
        {
            String result = "";
            switch (br.Stack.ToLower()) {
                case "dotnetcore":
                    result = await HttpDotnetcorePipeline.Run(br, log);
                    break;
                case "node":
                    result = await HttpNodePipeline.Run(br, log);
                    break;
                case "php":
                    result = await HttpPhpPipeline.Run(br, log);
                    break;
                case "python":
                    result = await HttpPythonPipeline.Run(br, log);
                    break;
                case "ruby":
                    result = await HttpRubyPipeline.Run(br, log);
                    break;
                case "kudu":
                    result = await HttpKuduPipeline.Run(br, log);
                    break;
            }
            log.LogInformation($"Result: result");
            
            return result;
        }

        [FunctionName("HttpBuildPipeline_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")]HttpRequestMessage req,
            [OrchestrationClient]DurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            String content = await req.Content.ReadAsStringAsync();

            string instanceId = await starter.StartNewAsync("HttpBuildPipeline", content);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }
}
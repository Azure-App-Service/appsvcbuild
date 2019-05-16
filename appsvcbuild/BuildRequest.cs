using System.Collections.Generic;
using Newtonsoft.Json;

namespace appsvcbuild
{
    public class BuildRequest
    {
        [JsonProperty("stack")]
        public string Stack;

        [JsonProperty("version")]
        public string Version;

        [JsonProperty("templateRepoURL")]
        public string TemplateRepoURL;

        [JsonProperty("templateRepoName")]
        public string TemplateRepoName;

        [JsonProperty("templateName")]
        public string TemplateName;

        [JsonProperty("branch")]
        public string Branch;

        [JsonProperty("baseImage")]
        public string BaseImage;

        [JsonProperty("outputRepoURL")]
        public string OutputRepoURL;

        [JsonProperty("outputRepoName")]
        public string OutputRepoName;

        [JsonProperty("outputImageName")]
        public string OutputImage;

        [JsonProperty("testWebAppName")]
        public string TestWebAppName;
    }
}

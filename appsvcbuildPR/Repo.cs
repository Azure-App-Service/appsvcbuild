using System.Collections.Generic;
using Newtonsoft.Json;

namespace appsvcbuildPR
{
    public class Repo
    {
        [JsonProperty("name")]
        public string name { get; set; }

        [JsonProperty("full_name")]
        public string full_name { get; set; }

        [JsonProperty("clone_url")]
        public string clone_url { get; set; }
    }
}

using System.Collections.Generic;
using Newtonsoft.Json;

namespace appsvcbuild
{
    public class Result
    {
        [JsonProperty("name")]
        public string Name;

        [JsonProperty("last_updated")]
        public string LastUpdated;
    }
}

using System.Collections.Generic;
using Newtonsoft.Json;

namespace appsvcbuild
{
    public class Tag
    {
        [JsonProperty("name")]
        public string Name;

        [JsonProperty("last_updated")]
        public string LastUpdated;
    }
}

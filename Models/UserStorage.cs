using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace planty_compare_fulfillment.Models
{
    [JsonObject]
    public class UserStorage
    {

        [JsonProperty(PropertyName = "userId")]
        public string UserId { get; set; }
    }
}

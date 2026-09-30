using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace X0.MultiPlay.Protocol
{
    public class NetworkMessage
    {
        [JsonProperty("t")]
        public MessageType Type { get; set; }

        [JsonProperty("p")]
        public JToken Payload { get; set; }

        [JsonProperty("ts")]
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public string Serialize() => JsonConvert.SerializeObject(this);

        public static NetworkMessage Deserialize(string json) =>
            JsonConvert.DeserializeObject<NetworkMessage>(json);
    }
}

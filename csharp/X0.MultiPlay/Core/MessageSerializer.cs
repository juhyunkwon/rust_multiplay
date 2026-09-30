using X0.MultiPlay.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace X0.MultiPlay
{
    public static class MessageSerializer
    {
        static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Include,
        };

        public static string Serialize(object obj) => JsonConvert.SerializeObject(obj, Settings);

        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        public static T Deserialize<T>(JToken token) => token == null ? default : token.ToObject<T>();

        public static NetworkMessage ParseNetworkMessage(string json) => NetworkMessage.Deserialize(json);

        public static JToken ToToken(object payload) =>
            payload == null ? new JObject() : JToken.FromObject(payload);
    }
}

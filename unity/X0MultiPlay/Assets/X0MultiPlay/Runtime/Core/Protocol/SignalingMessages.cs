using Newtonsoft.Json;

namespace X0.MultiPlay.Protocol
{
    public class RegisterRequest
    {
        [JsonProperty("id")] public string Id { get; set; } = System.Guid.NewGuid().ToString("N").Substring(0, 8);
        [JsonProperty("name")] public string Name { get; set; } = "Player";
        [JsonProperty("localPort")] public int LocalPort { get; set; }
        [JsonProperty("roomNumber")] public int RoomNumber { get; set; } = 1;
        [JsonProperty("localIp")] public string LocalIp { get; set; } = "";
        [JsonProperty("userId")] public string UserId { get; set; } = "";
        [JsonProperty("dedicatedRoomId")] public string DedicatedRoomId { get; set; } = "";
    }

    public class RegisterResponse
    {
        [JsonProperty("registeredId")] public string RegisteredId { get; set; } = "";
        [JsonProperty("publicIp")] public string PublicIp { get; set; } = "";
        [JsonProperty("publicPort")] public int PublicPort { get; set; }
        [JsonProperty("isMaster")] public bool IsMaster { get; set; }
        [JsonProperty("udpToken")] public string UdpToken { get; set; } = "";
    }

    public class PeerInfo
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("publicIp")] public string PublicIp { get; set; } = "";
        [JsonProperty("publicPort")] public int PublicPort { get; set; }
        [JsonProperty("isMaster")] public bool IsMaster { get; set; }
        [JsonProperty("localIp")] public string LocalIp { get; set; } = "";
        [JsonProperty("localPort")] public int LocalPort { get; set; }
        [JsonProperty("userId")] public string UserId { get; set; } = "";
        [JsonProperty("dedicatedRoomId")] public string DedicatedRoomId { get; set; } = "";
    }

    public class ConnectRequestPayload
    {
        [JsonProperty("peerId")] public string PeerId { get; set; } = "";
        [JsonProperty("requesterId")] public string RequesterId { get; set; } = "";
        [JsonProperty("requesterInfo")] public PeerInfo RequesterInfo { get; set; }
    }

    public class ConnectResponsePayload
    {
        [JsonProperty("accepted")] public bool Accepted { get; set; }
        [JsonProperty("peerId")] public string PeerId { get; set; } = "";
        [JsonProperty("requesterInfo")] public PeerInfo RequesterInfo { get; set; }
    }
}

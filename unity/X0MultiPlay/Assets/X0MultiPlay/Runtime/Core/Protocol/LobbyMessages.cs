using System;
using Newtonsoft.Json;

namespace X0.MultiPlay.Protocol
{
    public class LoginRequest
    {
        [JsonProperty("userId")] public string UserId { get; set; } = "";
        [JsonProperty("password")] public string Password { get; set; } = "";
        [JsonProperty("shopId")] public long ShopId { get; set; }
    }

    public class LoginResponse
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("message")] public string Message { get; set; } = "";
        [JsonProperty("user")] public UserInfo User { get; set; }
        [JsonProperty("connectionId")] public string ConnectionId { get; set; } = "";
    }

    public class UserInfo
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("userId")] public string UserId { get; set; } = "";
        [JsonProperty("userName")] public string UserName { get; set; } = "";
        [JsonProperty("displayName")] public string DisplayName { get; set; } = "";
        // M, F, 없으면 빈 값
        [JsonProperty("gender")] public string Gender { get; set; } = "";
    }

    public class RoomInfo
    {
        [JsonProperty("roomId")] public string RoomId { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("hostId")] public string HostId { get; set; } = "";
        [JsonProperty("hostName")] public string HostName { get; set; } = "";
        [JsonProperty("playerCount")] public int PlayerCount { get; set; }
        [JsonProperty("maxPlayers")] public int MaxPlayers { get; set; }
        [JsonProperty("isPlaying")] public bool IsPlaying { get; set; }
    }

    public static class RoomLimits
    {
        public const int MaxPlayersPerRoom = 1000;
    }

    public class CreateRoomRequest
    {
        [JsonProperty("roomName")] public string RoomName { get; set; } = "";
        [JsonProperty("maxPlayers")] public int MaxPlayers { get; set; } = 6;
    }

    public class JoinRoomRequest
    {
        [JsonProperty("roomId")] public string RoomId { get; set; } = "";
    }
}

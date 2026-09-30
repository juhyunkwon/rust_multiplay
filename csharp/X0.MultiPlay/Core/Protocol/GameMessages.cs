using System.Collections.Generic;
using Newtonsoft.Json;

namespace X0.MultiPlay.Protocol
{
    public class GameStatePayload
    {
        [JsonProperty("serverTick")] public long ServerTick { get; set; }
        [JsonProperty("players")] public List<PlayerState> Players { get; set; } = new();
    }

    public class PlayerState
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("x")] public float X { get; set; }
        [JsonProperty("y")] public float Y { get; set; }
        [JsonProperty("health")] public int Health { get; set; }
        [JsonProperty("lastInputTick")] public long LastInputTick { get; set; }
    }

    public class PlayerInputPayload
    {
        [JsonProperty("playerId")] public string PlayerId { get; set; } = "";
        [JsonProperty("inputTick")] public long InputTick { get; set; }
        [JsonProperty("moveX")] public float MoveX { get; set; }
        [JsonProperty("moveY")] public float MoveY { get; set; }
        [JsonProperty("action")] public string Action { get; set; } = "";
    }
}

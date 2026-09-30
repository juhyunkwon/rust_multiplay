using System.IO;
using Newtonsoft.Json.Linq;
using X0.MultiPlay.Client;

namespace X0.MultiPlay.Gui;

sealed class GuiSettings
{
    public DedicatedEndpoint DedicatedServer { get; set; } = new();
    public SignalingEndpoint SignalingServer { get; set; } = new();
    public EncryptionSettings Encryption { get; set; } = new();
    public LoginSettings Login { get; set; } = new() { UserId = "boy1", Password = "1111" };
    public LoginSettings LoginB { get; set; } = new() { UserId = "boy2", Password = "1111" };

    public string LoadedFrom { get; private set; } = "";

    public static GuiSettings Load()
    {
        var path = ConfigFile.Find();
        if (path == null) return new GuiSettings { LoadedFrom = "(기본값, config/appsettings.json 없음)" };
        var loaded = JObject.Parse(File.ReadAllText(path)).ToObject<GuiSettings>() ?? new GuiSettings();
        loaded.LoadedFrom = path;
        return loaded;
    }
}

sealed class DedicatedEndpoint
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 19060;
}

sealed class SignalingEndpoint
{
    public string Host { get; set; } = "127.0.0.1";
    public int TcpPort { get; set; } = 19050;
    public int UdpPort { get; set; } = 19051;
}

sealed class EncryptionSettings
{
    public string Key { get; set; } = "";
}

sealed class LoginSettings
{
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
    public long ShopId { get; set; }
}

using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace X0.MultiPlay
{
    [Serializable]
    public class ClientSettings
    {
        public DedicatedServerSettings DedicatedServer = new();
        public SignalingServerSettings SignalingServer = new();
        public EncryptionSettings Encryption = new();
        public LoginSettings Login = new();
        public LoginSettings LoginB = new();

        [Serializable]
        public class DedicatedServerSettings
        {
            public string Host = "127.0.0.1";
            public int Port = 19060;
        }

        [Serializable]
        public class SignalingServerSettings
        {
            public string Host = "127.0.0.1";
            public int TcpPort = 19050;
            public int UdpPort = 19051;
        }

        [Serializable]
        public class EncryptionSettings
        {
            public string Key = "";
        }

        [Serializable]
        public class LoginSettings
        {
            public string UserId = "";
            public string Password = "";
            public long ShopId;
        }

        public string DedicatedAddress => $"{DedicatedServer.Host}:{DedicatedServer.Port}";

        public static ClientSettings LoadOrDefault()
        {
            try
            {
                var path = FindConfigFile();
                if (path != null)
                {
                    var loaded = JsonConvert.DeserializeObject<ClientSettings>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        Debug.Log("[x0] 설정: " + path);
                        return loaded;
                    }
                }
                Debug.LogWarning("[x0] config/appsettings.json 못 찾음, 기본값 사용");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[x0] config/appsettings.json 읽기 실패: " + ex.Message);
            }
            return new ClientSettings();
        }

        // 서버랑 같은 config/appsettings.json 을 씀
        // CONFIG_PATH 먼저, 없으면 에디터는 프로젝트 폴더, 빌드는 exe 폴더부터 위로 올라가며 찾음
        static string FindConfigFile()
        {
            var env = Environment.GetEnvironmentVariable("CONFIG_PATH");
            if (!string.IsNullOrWhiteSpace(env))
            {
                if (File.Exists(env)) return env;
                var inDir = Path.Combine(env, "appsettings.json");
                if (File.Exists(inDir)) return inDir;
            }

            var dir = Directory.GetParent(Application.dataPath);
            for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "config", "appsettings.json");
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }
}

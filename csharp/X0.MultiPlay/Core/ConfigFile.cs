using System;
using System.Collections.Generic;
using System.IO;

namespace X0.MultiPlay.Client
{
    // 서버랑 같은 config/appsettings.json 을 씀
    // CONFIG_PATH(폴더나 파일) 먼저, 없으면 exe 위치/현재 폴더에서 위로 올라가며 찾음
    public static class ConfigFile
    {
        public const string FileName = "appsettings.json";

        public static string Find(params string[] extraStarts)
        {
            var env = Environment.GetEnvironmentVariable("CONFIG_PATH");
            if (!string.IsNullOrWhiteSpace(env))
            {
                if (File.Exists(env)) return env;
                var inDir = Path.Combine(env, FileName);
                if (File.Exists(inDir)) return inDir;
            }

            var starts = new List<string> { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
            starts.AddRange(extraStarts);
            foreach (var start in starts)
            {
                var found = FindUpward(start);
                if (found != null) return found;
            }
            return null;
        }

        static string FindUpward(string start)
        {
            var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
            for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "config", FileName);
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }
}

using System;
using System.IO;

namespace X0.MultiPlay.Gui;

static class SessionLog
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "_log")),
        $"gui-{Environment.ProcessId}.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // ignore
        }
    }
}

static class CrashLog
{
    public static void Write(Exception ex)
    {
        var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + ex + Environment.NewLine;
        foreach (var path in Paths())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, text + Environment.NewLine);
            }
            catch
            {
                // ignore
            }
        }
    }

    static string[] Paths()
    {
        var name = "gui-crash.log";
        return new[]
        {
            Path.Combine(AppContext.BaseDirectory, "startup-error.txt"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "_log", name),
            Path.Combine(Directory.GetCurrentDirectory(), "_log", name)
        };
    }
}

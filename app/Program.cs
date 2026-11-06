using System;
using System.IO;
using System.Windows.Forms;

namespace Solara;

internal static class Log
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SOLARA_DEBUG") == "1";

    private static readonly string[] Targets = BuildTargets();

    private static string[] BuildTargets()
    {
        if (!Enabled) return Array.Empty<string>();
        var list = new System.Collections.Generic.List<string>();
        try { list.Add(Path.Combine(AppContext.BaseDirectory, "boot.log")); } catch { }
        try { list.Add(Path.Combine(Path.GetTempPath(), "solara-boot.log")); } catch { }
        return list.ToArray();
    }

    public static void Write(string message)
    {
        if (!Enabled) return;
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine;
        foreach (var path in Targets)
        {
            try { File.AppendAllText(path, line); } catch { }
        }
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        EnsureWritableTemp();
        Log.Write("=== main start === " + Environment.Version);
        try
        {
            ApplicationConfiguration.Initialize();
            Log.Write("ApplicationConfiguration ok");

            Application.ThreadException += (_, e) =>
            {
                Log.Write("ThreadException: " + e.Exception);
                ShowFatal(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Log.Write("Unhandled: " + e.ExceptionObject);
                ShowFatal(e.ExceptionObject as Exception);
            };

            var form = new MainForm();
            Log.Write("MainForm created");
            Application.Run(form);
            Log.Write("=== exit normal ===");
        }
        catch (Exception ex)
        {
            Log.Write("FATAL: " + ex);
            ShowFatal(ex);
        }
    }

    private static void ShowFatal(Exception? ex)
    {
        try
        {
            MessageBox.Show(ex?.ToString() ?? "未知错误", "SOLARA 运行错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // 连弹窗都失败就安静退出
        }
    }

    /// <summary>
    /// WinForms 启用视觉样式时会在 %TEMP% 写临时清单文件；某些受限环境里
    /// 那个目录不可写，会直接抛 UnauthorizedAccessException。这里探测一次，
    /// 不可写就把 TEMP/TMP 指到程序自己目录下的 Temp。
    /// </summary>
    private static void EnsureWritableTemp()
    {
        try
        {
            var probe = Path.Combine(Path.GetTempPath(), "solara-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return;
        }
        catch
        {
            // 落到下面的兜底
        }

        try
        {
            var alt = Path.Combine(AppContext.BaseDirectory, "Temp");
            Directory.CreateDirectory(alt);
            var probe = Path.Combine(alt, "solara-probe.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            Environment.SetEnvironmentVariable("TEMP", alt);
            Environment.SetEnvironmentVariable("TMP", alt);
            Log.Write("using fallback TEMP: " + alt);
        }
        catch
        {
            // 实在没有可写目录，交给上层报错
        }
    }
}

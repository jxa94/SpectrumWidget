using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace SpectrumWidget;

public partial class App : Application
{
    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Windows 从 Run 键启动时有时会丢掉参数，所以开机 3 分钟内启动的也算自启
        bool autostart = Array.IndexOf(e.Args, "--autostart") >= 0 || Environment.TickCount64 < 3 * 60 * 1000;
        _mutex = new Mutex(true, "SpectrumWidget.SingleInstance", out bool created);
        LogLine($"启动 {(autostart ? "（开机自启）" : "（手动）")}，开机后 {TimeSpan.FromMilliseconds(Environment.TickCount64):hh\\:mm\\:ss}{(created ? "" : "，已有实例在运行，退出")}");
        if (!created)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);

        base.OnStartup(e);
        SpectrumWidget.MainWindow.RefreshAutostartPath();
        new MainWindow(Settings.Load()).Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        LogLine($"系统{(e.ReasonSessionEnding == ReasonSessionEnding.Shutdown ? "关机 / 重启" : "注销")}，保存设置");
        foreach (Window w in Windows) w.Close();
        base.OnSessionEnding(e);
    }

    /// <summary>启动 / 退出等事件记到 launch.log，只保留最近 200 行。</summary>
    internal static void LogLine(string msg)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            string path = Path.Combine(Settings.Dir, "launch.log");
            var lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
            var keep = lines.Length >= 200 ? lines[^199..] : lines;
            File.WriteAllLines(path, keep.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}"));
        }
        catch (Exception ex)
        {
            // 写不进 launch.log 时至少在 error.log 里留下原因
            Log(new IOException($"launch.log 写入失败：{msg}", ex));
        }
    }

    internal static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}

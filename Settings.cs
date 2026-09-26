using System;
using System.IO;
using System.Text.Json;

namespace SpectrumWidget;

public enum VisualStyle { Bars, Mirror, Wave }

public sealed class Settings
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpectrumWidget");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Topmost { get; set; }
    public bool Locked { get; set; }
    public bool ClickThrough { get; set; }
    public double Scale { get; set; } = 1.0;
    public double BackgroundOpacity { get; set; } = 0.95;
    public VisualStyle Style { get; set; } = VisualStyle.Bars;
    public int BarCount { get; set; } = 48;
    public bool AutoHideIdle { get; set; }
    public int AutoHideDelay { get; set; } = 10;
    public bool AutoHideFullscreen { get; set; }
    public bool HideTipShown { get; set; }
    public bool Collapsed { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // 先写临时文件再替换，关机时被中途打断也不会留下半截文件
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }
}

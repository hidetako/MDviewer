using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdViewer;

/// <summary>
/// %APPDATA%\MdViewer\settings.json に保存するユーザ設定。
/// </summary>
public sealed class AppSettings
{
    public const int MaxRecent = 12;

    public string Theme { get; set; } = "light";
    public double Zoom { get; set; } = 1.0;
    public bool TocVisible { get; set; } = true;
    public List<string> Recent { get; set; } = new();
    public WindowState Window { get; set; } = new();

    public sealed class WindowState
    {
        public int X { get; set; } = -1;
        public int Y { get; set; } = -1;
        public int Width { get; set; } = 1200;
        public int Height { get; set; } = 820;
        public bool Maximized { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MdViewer");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (s != null)
                {
                    s.Recent = s.Recent.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRecent).ToList();
                    if (s.Zoom < 0.5 || s.Zoom > 3) s.Zoom = 1.0;
                    if (s.Theme != "dark" && s.Theme != "light") s.Theme = "light";
                    return s;
                }
            }
        }
        catch
        {
            // 壊れた設定ファイルは無視して既定値で起動
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 設定保存失敗は致命的ではない
        }
    }

    public void TouchRecent(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
    }
}

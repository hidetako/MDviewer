using System.Reflection;

namespace MdViewer;

/// <summary>
/// exe に埋め込まれた Assets/ (index.html, app.js, vendor/...) を
/// %LOCALAPPDATA%\MdViewer\assets\&lt;version&gt;\ へ展開する。
/// WebView2 の SetVirtualHostNameToFolderMapping は実フォルダを要求するため。
/// </summary>
internal static class AssetStore
{
    private const string Prefix = "Assets/";

    public static string LocalAppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdViewer");

    public static string EnsureExtracted()
    {
        var asm = typeof(AssetStore).Assembly;
        var dir = Path.Combine(LocalAppDir, "assets", Program.Version);
        var marker = Path.Combine(dir, ".extracted");

        // 開発時: 同じバージョンでも Assets の内容が変わりうるので、
        // 埋め込みリソースのサイズ合計をマーカーに記録して差分検知する
        var names = asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        long total = 0;
        foreach (var n in names)
        {
            using var s = asm.GetManifestResourceStream(n);
            if (s != null) total += s.Length;
        }
        var stamp = $"{names.Length}:{total}";

        if (File.Exists(marker) && File.ReadAllText(marker) == stamp)
            return dir;

        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        foreach (var name in names)
        {
            var rel = name.Substring(Prefix.Length).Replace('\\', '/');
            var target = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }

        File.WriteAllText(marker, stamp);

        // 古いバージョンの展開物を掃除 (失敗しても無視)
        try
        {
            foreach (var old in Directory.GetDirectories(Path.Combine(LocalAppDir, "assets")))
            {
                if (!string.Equals(old, dir, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(old, recursive: true);
            }
        }
        catch { /* ignore */ }

        return dir;
    }
}

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace MdViewer;

/// <summary>
/// GitHub Releases を使った自動更新。
///
///   Check()  : /releases/latest を取得し、現在より新しいバージョンがあれば UpdateInfo を返す
///   Apply()  : 配布物をダウンロード → SHA256SUMS.txt で検証 → 適用
///              - インストーラ版 (Inno Setup の uninstall キーが exe の場所を指す) : Setup.exe をサイレント実行して再起動
///              - ポータブル版 : 実行中の exe を .old にリネームし、新 exe を置いて再起動
///
/// ネットワーク不通・API 制限・検証失敗などは全て例外 → 呼び出し側で握りつぶして通常起動を続ける。
/// </summary>
internal sealed class UpdateService
{
    public const string Owner = "hidetako";
    public const string Repo = "MDviewer";
    private const string LatestApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    private const string InnoAppId = "{B7A4F0B2-5C7D-4E0B-9B9A-6D3E2F1A8C11}";
    private const string SetupAssetPrefix = "MdViewer-Setup-";
    private const string PortableAssetName = "MdViewer.exe";
    private const string ChecksumAssetName = "SHA256SUMS.txt";

    public sealed record UpdateInfo(
        Version Version,
        string TagName,
        string ReleaseUrl,
        string AssetName,
        string AssetUrl,
        long AssetSize,
        string? ChecksumUrl,
        InstallKind Kind);

    public enum InstallKind { Portable, InstalledCurrentUser, InstalledAllUsers }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MdViewer", Program.Version));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    public static string UpdateDir => Path.Combine(AssetStore.LocalAppDir, "updates");

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    // ------------------------------------------------------------------ check

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        using var res = await Http.SendAsync(req, cts.Token);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var latest)) return null;
        if (latest <= CurrentVersion) return null;

        var releaseUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";
        var kind = DetectInstallKind();

        string? assetName = null, assetUrl = null, checksumUrl = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name == ChecksumAssetName) checksumUrl = url;
                var wanted = kind == InstallKind.Portable
                    ? name.Equals(PortableAssetName, StringComparison.OrdinalIgnoreCase)
                    : name.StartsWith(SetupAssetPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (wanted)
                {
                    assetName = name;
                    assetUrl = url;
                    assetSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                }
            }
        }
        if (assetName == null || assetUrl == null) return null; // この形態向けの配布物が無い

        return new UpdateInfo(latest, tag, releaseUrl, assetName, assetUrl, assetSize, checksumUrl, kind);
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var dash = s.IndexOf('-');
        if (dash > 0) s = s[..dash]; // v1.2.3-beta → 1.2.3
        if (Version.TryParse(s, out var v) && v.Major >= 0)
        {
            version = new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Inno Setup の uninstall キー (InstallLocation) が exe の場所を指していればインストーラ版。</summary>
    public static InstallKind DetectInstallKind()
    {
        var exeDir = Path.GetDirectoryName(ExePath) ?? "";
        if (PointsHere(Registry.CurrentUser, exeDir)) return InstallKind.InstalledCurrentUser;
        if (PointsHere(Registry.LocalMachine, exeDir)) return InstallKind.InstalledAllUsers;
        return InstallKind.Portable;
    }

    private static bool PointsHere(RegistryKey hive, string exeDir)
    {
        foreach (var sub in new[]
                 {
                     $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{InnoAppId}_is1",
                     $@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{InnoAppId}_is1",
                 })
        {
            try
            {
                using var k = hive.OpenSubKey(sub);
                if (k?.GetValue("InstallLocation") is string loc && !string.IsNullOrEmpty(loc))
                {
                    var a = Path.GetFullPath(loc).TrimEnd(Path.DirectorySeparatorChar);
                    var b = Path.GetFullPath(exeDir).TrimEnd(Path.DirectorySeparatorChar);
                    if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { /* ignore */ }
        }
        return false;
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// ダウンロード → 検証 → 適用。成功時はアプリを終了させる必要があるため true を返す (呼び出し側で Close)。
    /// </summary>
    public static async Task<bool> DownloadAndApplyAsync(UpdateInfo info, string? reopenFile, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdateDir);
        var target = Path.Combine(UpdateDir, info.AssetName);
        var tmp = target + ".part";

        // 1. 期待ハッシュ (必須: 無ければ更新しない)
        if (info.ChecksumUrl == null) throw new InvalidOperationException("リリースに SHA256SUMS.txt が無いため更新を中止しました。");
        var sums = await Http.GetStringAsync(info.ChecksumUrl, ct);
        var expected = ParseChecksum(sums, info.AssetName)
                       ?? throw new InvalidOperationException($"SHA256SUMS.txt に {info.AssetName} の項目がありません。");

        // 2. ダウンロード
        using (var res = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? info.AssetSize;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(Math.Min(0.99, (double)done / total));
            }
        }

        // 3. 検証
        var actual = await Sha256Async(tmp, ct);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException("ダウンロードしたファイルの SHA-256 が一致しません。更新を中止しました。");
        }
        File.Move(tmp, target, overwrite: true);
        progress.Report(1.0);

        // 4. 適用
        return info.Kind == InstallKind.Portable
            ? ApplyPortable(target, reopenFile)
            : ApplyInstaller(target, info.Kind, reopenFile);
    }

    private static bool ApplyInstaller(string setupExe, InstallKind kind, string? reopenFile)
    {
        var args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1";
        args += kind == InstallKind.InstalledAllUsers ? " /ALLUSERS" : " /CURRENTUSER";
        if (!string.IsNullOrEmpty(reopenFile)) args += $" \"/RELAUNCHFILE={reopenFile}\"";
        Process.Start(new ProcessStartInfo(setupExe, args) { UseShellExecute = true });
        return true; // 呼び出し側でアプリを閉じる → インストーラが上書き → [Run] で再起動
    }

    private static bool ApplyPortable(string newExe, string? reopenFile)
    {
        var exe = ExePath;
        var old = exe + ".old";
        try { File.Delete(old); } catch { }
        // 実行中の exe はリネームできる (削除・上書きは不可)
        File.Move(exe, old);
        try
        {
            File.Move(newExe, exe);
        }
        catch
        {
            File.Move(old, exe); // 戻す
            throw;
        }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(reopenFile)) psi.ArgumentList.Add(reopenFile);
        Process.Start(psi);
        return true;
    }

    /// <summary>前回の自己更新で残った MdViewer.exe.old を消す (起動時に呼ぶ)。</summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            var old = ExePath + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* まだロックされていれば次回 */ }
        try
        {
            if (Directory.Exists(UpdateDir))
                foreach (var f in Directory.GetFiles(UpdateDir))
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
        }
        catch { /* ignore */ }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>sha256sum 形式 ("&lt;hash&gt;  &lt;filename&gt;") から該当ファイルのハッシュを取り出す。</summary>
    public static string? ParseChecksum(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66) continue;
            var hash = line[..64];
            var name = line[64..].TrimStart(' ', '*').Trim();
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) && IsHex(hash)) return hash;
        }
        return null;
    }

    private static bool IsHex(string s) => s.All(c => Uri.IsHexDigit(c));

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}

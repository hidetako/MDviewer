using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MdViewer;

/// <summary>
/// HKCU (ユーザ単位・管理者権限不要) に .md / .markdown の「プログラムから開く」候補を登録する。
///
/// 注意: Windows 10/11 では既定アプリの最終決定はユーザ操作 (UserChoice) が優先されるため、
/// ここで登録しても「既定」になるとは限らない。登録後に Windows の設定画面を開いて選んでもらう。
/// </summary>
internal static class FileAssociation
{
    public const string ProgId = "MdViewer.Markdown";
    public static readonly string[] Extensions = { ".md", ".markdown", ".mdown", ".mkd" };

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static void Register()
    {
        var exe = ExePath;
        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", "Markdown 文書");
            prog.SetValue("FriendlyTypeName", "Markdown 文書");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{exe}\" \"%1\"");
            using (var open = prog.CreateSubKey(@"shell\open")) open.SetValue("", "MD Viewer で開く");
        }

        // 「プログラムから開く」一覧用 (Applications\MdViewer.exe)
        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\MdViewer.exe"))
        {
            app.SetValue("FriendlyAppName", "MD Viewer");
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{exe}\" \"%1\"");
            using (var types = app.CreateSubKey("SupportedTypes"))
                foreach (var ext in Extensions) types.SetValue(ext, "");
        }

        foreach (var ext in Extensions)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}");
            // 既定 ProgId が未設定の場合のみ設定 (ユーザの既存選択は上書きしない)
            if (key.GetValue("") is not string existing || string.IsNullOrEmpty(existing))
                key.SetValue("", ProgId);
            using var owp = key.CreateSubKey("OpenWithProgids");
            owp.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NotifyShell();
    }

    public static void Unregister()
    {
        foreach (var ext in Extensions)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}", writable: true);
                if (key == null) continue;
                if (key.GetValue("") is string v && v == ProgId) key.DeleteValue("", throwOnMissingValue: false);
                using var owp = key.OpenSubKey("OpenWithProgids", writable: true);
                owp?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
            catch { /* ignore */ }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\MdViewer.exe", throwOnMissingSubKey: false); } catch { }
        NotifyShell();
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            return key?.GetValue("") is string s && s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShell()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); } catch { }
    }
}

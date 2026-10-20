using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MdViewer;

/// <summary>
/// メインウィンドウ。UI は WebView2 内の HTML (Assets/index.html) が担当し、
/// ここではファイル I/O・ダイアログ・OS 連携のみ行う。
/// </summary>
public sealed class MainForm : Form
{
    private const string AppHost = "app.mdviewer";
    private const string DocHost = "doc.mdviewer";
    private static readonly string[] MarkdownExtensions = { ".md", ".markdown", ".mdown", ".mkd", ".txt" };
    private static readonly string[] ShellOpenableExtensions =
        { ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".html", ".htm", ".csv", ".xlsx", ".xls", ".docx", ".doc", ".pptx", ".ppt", ".json", ".yaml", ".yml" };

    private readonly WebView2 _web = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly System.Windows.Forms.Timer _reloadTimer = new() { Interval = 400 };

    private string? _pendingPath;
    private string? _pendingAnchor;
    private string? _currentPath;
    private FileSystemWatcher? _watcher;
    private bool _webReady;

    public MainForm(string? initialPath)
    {
        _pendingPath = initialPath;

        Text = "MD Viewer";
        MinimumSize = new Size(480, 320);
        RestoreWindowBounds();
        TrySetIcon();

        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        _web.Dock = DockStyle.Fill;
        // ドロップはホスト (WinForms) 側で受ける → フルパスが取れる。
        // WebView2 側が OLE drop target を登録しなければ、OLE は親ウィンドウ (この Form) の drop target を探す。
        _web.AllowExternalDrop = false;
        ((Control)_web).AllowDrop = true; // WebView2 は AllowDrop を読み取り専用で隠しているため基底 Control 経由で設定
        _web.DragEnter += OnDragEnter;
        _web.DragDrop += OnDragDrop;
        Controls.Add(_web);

        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            if (_currentPath != null && File.Exists(_currentPath)) OpenFile(_currentPath, keepScroll: true);
        };

        Load += async (_, _) => await InitializeWebViewAsync();
        FormClosing += (_, _) => SaveWindowBounds();
    }

    // ------------------------------------------------------------------ WebView2 init

    private async Task InitializeWebViewAsync()
    {
        string assetsDir;
        try
        {
            assetsDir = AssetStore.EnsureExtracted();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "内部ファイルの展開に失敗しました。\n\n" + ex.Message, "MD Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        try
        {
            var userData = Path.Combine(AssetStore.LocalAppDir, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions { Language = "ja" });
            await _web.EnsureCoreWebView2Async(env);
            _web.AllowExternalDrop = false;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var r = MessageBox.Show(this,
                "Microsoft Edge WebView2 ランタイムが見つかりません。\n" +
                "Windows 10/11 には通常インストール済みですが、無い場合は Microsoft のサイトから無料で入手できます。\n\n" +
                "ダウンロードページを開きますか？",
                "MD Viewer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");
            Close();
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "WebView2 の初期化に失敗しました。\n\n" + ex.Message, "MD Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = true;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;          // 文字サイズは自前 (CSS) で制御
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+P / F5 / Ctrl+F などは JS 側で処理
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif

        core.SetVirtualHostNameToFolderMapping(AppHost, assetsDir, CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.Navigate($"https://{AppHost}/index.html");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // アプリ内ページ以外へのトップレベル遷移は全て阻止 (外部リンクは OS ブラウザへ)
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, AppHost, StringComparison.OrdinalIgnoreCase))
            return;
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    // ------------------------------------------------------------------ Web → Host messages

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.TryGetWebMessageAsString(); }
        catch { return; }
        if (string.IsNullOrEmpty(json)) return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return; }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "ready":
                    _webReady = true;
                    PostInit();
                    if (_pendingPath != null)
                    {
                        var p = _pendingPath; var a = _pendingAnchor;
                        _pendingPath = null; _pendingAnchor = null;
                        OpenFile(p, anchor: a);
                    }
                    break;
                case "open":
                    ShowOpenDialog();
                    break;
                case "openPath":
                    if (root.TryGetProperty("path", out var pp) && pp.GetString() is { } path) OpenFile(path);
                    break;
                case "reload":
                    if (_currentPath != null) OpenFile(_currentPath, keepScroll: true);
                    break;
                case "openExternal":
                    if (root.TryGetProperty("url", out var u) && u.GetString() is { } url) OpenExternal(url);
                    break;
                case "openRelative":
                    if (root.TryGetProperty("href", out var h) && h.GetString() is { } href) OpenRelative(href);
                    break;
                case "print":
                    try { _web.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); }
                    catch (Exception ex) { Toast("印刷ダイアログを開けませんでした: " + ex.Message); }
                    break;
                case "pdf":
                    _ = ExportPdfAsync();
                    break;
                case "settings":
                    if (root.TryGetProperty("settings", out var st)) ApplySettingsFromWeb(st);
                    break;
                case "clearRecent":
                    _settings.Recent.Clear();
                    _settings.Save();
                    Post(new { type = "recent", recent = _settings.Recent });
                    break;
                case "register":
                    RegisterAssociation();
                    break;
                case "unregister":
                    try { FileAssociation.Unregister(); Toast("既定アプリ登録を解除しました"); }
                    catch (Exception ex) { Toast("解除に失敗しました: " + ex.Message); }
                    break;
                case "openFolder":
                    if (_currentPath != null && File.Exists(_currentPath))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentPath}\"") { UseShellExecute = false });
                    break;
                case "clipboard":
                    if (root.TryGetProperty("text", out var tx) && tx.GetString() is { } text)
                    {
                        try { Clipboard.SetText(text); Toast("コピーしました"); }
                        catch (Exception ex) { Toast("コピーに失敗しました: " + ex.Message); }
                    }
                    break;
            }
        }
    }

    private void PostInit()
    {
        Post(new
        {
            type = "init",
            version = Program.Version,
            settings = new
            {
                theme = _settings.Theme,
                zoom = _settings.Zoom,
                tocVisible = _settings.TocVisible,
                recent = _settings.Recent,
            },
        });
    }

    private void ApplySettingsFromWeb(JsonElement st)
    {
        if (st.TryGetProperty("theme", out var th) && th.GetString() is "light" or "dark") _settings.Theme = th.GetString()!;
        if (st.TryGetProperty("zoom", out var z) && z.TryGetDouble(out var zoom) && zoom is >= 0.5 and <= 3) _settings.Zoom = zoom;
        if (st.TryGetProperty("tocVisible", out var tv) && (tv.ValueKind == JsonValueKind.True || tv.ValueKind == JsonValueKind.False)) _settings.TocVisible = tv.GetBoolean();
        _settings.Save();
    }

    private void Post(object message)
    {
        if (!_webReady || _web.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch { /* window closing etc. */ }
    }

    private void Toast(string message) => Post(new { type = "toast", message });

    // ------------------------------------------------------------------ file open

    private void ShowOpenDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Markdown ファイルを開く",
            Filter = "Markdown (*.md;*.markdown;*.mdown;*.mkd;*.txt)|*.md;*.markdown;*.mdown;*.mkd;*.txt|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
        };
        if (_currentPath != null) dlg.InitialDirectory = Path.GetDirectoryName(_currentPath);
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenFile(dlg.FileName);
    }

    public void OpenFile(string path, bool keepScroll = false, string? anchor = null)
    {
        if (!_webReady)
        {
            _pendingPath = path;
            _pendingAnchor = anchor;
            return;
        }

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { Post(new { type = "error", message = "パスが不正です:\n" + path + "\n" + ex.Message }); return; }

        if (!File.Exists(full))
        {
            Post(new { type = "error", message = "ファイルが見つかりません:\n" + full });
            _settings.Recent.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            Post(new { type = "recent", recent = _settings.Recent });
            return;
        }

        string text, encodingName;
        try { (text, encodingName) = TextFile.Read(full); }
        catch (IOException)
        {
            // エディタ保存直後のロック競合 → 少し待って再試行
            _reloadTimer.Stop();
            _reloadTimer.Start();
            return;
        }
        catch (Exception ex)
        {
            Post(new { type = "error", message = "読み込みに失敗しました:\n" + full + "\n" + ex.Message });
            return;
        }

        var dir = Path.GetDirectoryName(full) ?? full;
        var core = _web.CoreWebView2;
        try { core.ClearVirtualHostNameToFolderMapping(DocHost); } catch { }
        core.SetVirtualHostNameToFolderMapping(DocHost, dir, CoreWebView2HostResourceAccessKind.DenyCors);

        _currentPath = full;
        Text = Path.GetFileName(full) + " — MD Viewer";
        _settings.TouchRecent(full);
        _settings.Save();

        Post(new
        {
            type = "load",
            path = full,
            name = Path.GetFileName(full),
            dir,
            text,
            encoding = encodingName,
            keepScroll,
            anchor,
        });
        Post(new { type = "recent", recent = _settings.Recent });

        WatchFile(full);
    }

    private void OpenRelative(string href)
    {
        if (_currentPath == null) return;
        var dir = Path.GetDirectoryName(_currentPath) ?? "";
        var hashIdx = href.IndexOf('#');
        var anchor = hashIdx >= 0 ? href[(hashIdx + 1)..] : null;
        var rel = hashIdx >= 0 ? href[..hashIdx] : href;
        rel = Uri.UnescapeDataString(rel).Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(rel)) return;

        string target;
        try { target = Path.GetFullPath(Path.Combine(dir, rel)); }
        catch { Toast("リンク先のパスが不正です: " + href); return; }

        var ext = Path.GetExtension(target).ToLowerInvariant();
        if (File.Exists(target) && MarkdownExtensions.Contains(ext))
        {
            OpenFile(target, anchor: anchor);
        }
        else if (Directory.Exists(target))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false });
        }
        else if (File.Exists(target) && ShellOpenableExtensions.Contains(ext))
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Toast("開けませんでした: " + ex.Message); }
        }
        else if (File.Exists(target))
        {
            // 実行ファイル等を Markdown のリンクから起動しない (安全側)
            Toast("この種類のファイルはビューアから開けません: " + Path.GetFileName(target));
        }
        else
        {
            Toast("リンク先が見つかりません: " + target);
        }
    }

    private void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { Toast("ブラウザを開けませんでした: " + ex.Message); }
    }

    // ------------------------------------------------------------------ file watcher

    private void WatchFile(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var w = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                SynchronizingObject = this,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler onChange = (_, _) => { _reloadTimer.Stop(); _reloadTimer.Start(); };
            w.Changed += onChange;
            w.Created += onChange;
            w.Renamed += (_, _) => { _reloadTimer.Stop(); _reloadTimer.Start(); };
            _watcher = w;
        }
        catch
        {
            // ネットワークドライブ等で監視できない場合は手動再読込のみ
        }
    }

    // ------------------------------------------------------------------ PDF

    private async Task ExportPdfAsync()
    {
        if (_currentPath == null) { Toast("先にファイルを開いてください"); return; }
        using var dlg = new SaveFileDialog
        {
            Title = "PDF に書き出し",
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(_currentPath) + ".pdf",
            InitialDirectory = Path.GetDirectoryName(_currentPath),
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var core = _web.CoreWebView2;
            var ps = core.Environment.CreatePrintSettings();
            ps.ShouldPrintBackgrounds = true;
            ps.MarginTop = 0.5; ps.MarginBottom = 0.5; ps.MarginLeft = 0.5; ps.MarginRight = 0.5;
            ps.HeaderTitle = Path.GetFileName(_currentPath);
            ps.ShouldPrintHeaderAndFooter = true;
            var ok = await core.PrintToPdfAsync(dlg.FileName, ps);
            Toast(ok ? "PDF を保存しました: " + dlg.FileName : "PDF の保存に失敗しました");
        }
        catch (Exception ex)
        {
            Toast("PDF の保存に失敗しました: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ association

    private void RegisterAssociation()
    {
        try
        {
            FileAssociation.Register();
            var r = MessageBox.Show(this,
                "「プログラムから開く」の候補に MD Viewer を登録しました。\n\n" +
                "Windows 10/11 では既定アプリの最終決定はユーザ操作が必要です。\n" +
                "Windows の「既定のアプリ」設定を開いて .md を MD Viewer にしますか？\n" +
                "(.md ファイルを右クリック → プログラムから開く → 常に使う でも可)",
                "MD Viewer", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Toast("登録に失敗しました: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ drag & drop

    private static void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var first = files.FirstOrDefault(f => MarkdownExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) ?? files[0];
            OpenFile(first);
        }
    }

    // ------------------------------------------------------------------ window state

    private void RestoreWindowBounds()
    {
        var w = _settings.Window;
        var size = new Size(Math.Max(w.Width, MinimumSize.Width), Math.Max(w.Height, MinimumSize.Height));
        Size = size;
        if (w.X >= 0 && w.Y >= 0)
        {
            var rect = new Rectangle(w.X, w.Y, size.Width, size.Height);
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(rect)))
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(w.X, w.Y);
            }
            else StartPosition = FormStartPosition.CenterScreen;
        }
        else StartPosition = FormStartPosition.CenterScreen;
        if (w.Maximized) WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var w = _settings.Window;
        w.Maximized = WindowState == FormWindowState.Maximized;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        w.X = b.X; w.Y = b.Y; w.Width = b.Width; w.Height = b.Height;
        _settings.Save();
    }

    private void TrySetIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null) Icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch { /* ignore */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher?.Dispose();
            _reloadTimer.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}

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

    private readonly bool _noUpdateCheck;
    private UpdateService.UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _updateCts;
    private bool _updateInProgress;

    private string? _pendingPath;
    private string? _pendingAnchor;
    private string? _currentPath;
    private string _currentEncoding = "UTF-8";
    private string _currentNewline = "\r\n";
    private bool _dirty;
    private bool _externalChangeNotified;
    private DateTime _suppressWatchUntil = DateTime.MinValue;
    private Action? _afterSave; // 「保存してから○○」の○○
    private FileSystemWatcher? _watcher;
    private bool _webReady;

    public MainForm(string? initialPath, bool noUpdateCheck = false)
    {
        _pendingPath = initialPath;
        _noUpdateCheck = noUpdateCheck;

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
            if (_currentPath == null || !File.Exists(_currentPath)) return;
            if (DateTime.UtcNow < _suppressWatchUntil) return; // 自分の保存による変更通知
            if (_dirty)
            {
                // 編集中の内容を外部変更で上書きしない
                if (!_externalChangeNotified)
                {
                    _externalChangeNotified = true;
                    Toast("ファイルが外部で変更されました。再読込 (F5) で読み直せます (編集中の内容は破棄されます)");
                }
                return;
            }
            OpenFile(_currentPath, keepScroll: true);
        };

        Load += async (_, _) => await InitializeWebViewAsync();
        FormClosing += OnFormClosing;
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
                    if (_settings.UpdateCheck && !_noUpdateCheck) _ = CheckForUpdateAsync(manual: false);
                    break;
                case "checkUpdate":
                    _ = CheckForUpdateAsync(manual: true);
                    break;
                case "applyUpdate":
                    _ = ApplyUpdateAsync();
                    break;
                case "skipUpdate":
                    if (_pendingUpdate != null)
                    {
                        _settings.SkippedVersion = _pendingUpdate.TagName;
                        _settings.Save();
                        _pendingUpdate = null;
                    }
                    break;
                case "dismissUpdate":
                    _pendingUpdate = null;
                    break;
                case "openReleases":
                    OpenExternal($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/releases");
                    break;
                case "open":
                    ConfirmDiscardThen(ShowOpenDialog);
                    break;
                case "openPath":
                    if (root.TryGetProperty("path", out var pp) && pp.GetString() is { } path)
                        ConfirmDiscardThen(() => OpenFile(path));
                    break;
                case "reload":
                    if (_currentPath != null)
                        ConfirmDiscardThen(() => OpenFile(_currentPath!, keepScroll: true), discardPrompt: true);
                    break;
                case "new":
                    ConfirmDiscardThen(NewDocument);
                    break;
                case "save":
                    if (root.TryGetProperty("text", out var st1) && st1.GetString() is { } text1) SaveDocument(text1, saveAs: false);
                    break;
                case "saveAs":
                    if (root.TryGetProperty("text", out var st2) && st2.GetString() is { } text2) SaveDocument(text2, saveAs: true);
                    break;
                case "dirty":
                    _dirty = root.TryGetProperty("dirty", out var d) && d.ValueKind == JsonValueKind.True;
                    if (!_dirty) _externalChangeNotified = false;
                    UpdateTitle();
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
                updateCheck = _settings.UpdateCheck,
                recent = _settings.Recent,
            },
        });
    }

    private void ApplySettingsFromWeb(JsonElement st)
    {
        if (st.TryGetProperty("theme", out var th) && th.GetString() is "light" or "dark") _settings.Theme = th.GetString()!;
        if (st.TryGetProperty("zoom", out var z) && z.TryGetDouble(out var zoom) && zoom is >= 0.5 and <= 3) _settings.Zoom = zoom;
        if (st.TryGetProperty("tocVisible", out var tv) && (tv.ValueKind == JsonValueKind.True || tv.ValueKind == JsonValueKind.False)) _settings.TocVisible = tv.GetBoolean();
        if (st.TryGetProperty("updateCheck", out var uc) && (uc.ValueKind == JsonValueKind.True || uc.ValueKind == JsonValueKind.False)) _settings.UpdateCheck = uc.GetBoolean();
        _settings.Save();
    }

    private void Post(object message)
    {
        if (!_webReady || _web.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch { /* window closing etc. */ }
    }

    private void Toast(string message) => Post(new { type = "toast", message });

    // ------------------------------------------------------------------ auto update

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updateInProgress) return;
        if (!manual) await Task.Delay(3000); // 起動直後の描画を優先
        try
        {
            var info = await UpdateService.CheckAsync(CancellationToken.None);
            if (info == null)
            {
                if (manual) Toast($"最新版です (v{Program.Version})");
                return;
            }
            if (!manual && string.Equals(_settings.SkippedVersion, info.TagName, StringComparison.OrdinalIgnoreCase))
                return; // ユーザがスキップした版
            _pendingUpdate = info;
            Post(new
            {
                type = "update",
                current = Program.Version,
                version = info.Version.ToString(),
                tag = info.TagName,
                url = info.ReleaseUrl,
                size = info.AssetSize,
                kind = info.Kind.ToString(),
                manual,
            });
        }
        catch (Exception ex)
        {
            if (manual) Toast("更新の確認に失敗しました: " + ex.Message);
            // 自動チェックの失敗は黙って無視 (オフライン等)
        }
    }

    private async Task ApplyUpdateAsync()
    {
        var info = _pendingUpdate;
        if (info == null || _updateInProgress) return;
        _updateInProgress = true;
        _updateCts = new CancellationTokenSource();
        var progress = new Progress<double>(p => Post(new { type = "updateProgress", percent = (int)Math.Round(p * 100) }));
        try
        {
            var restart = await UpdateService.DownloadAndApplyAsync(info, _currentPath, progress, _updateCts.Token);
            if (restart)
            {
                _settings.Save();
                Close(); // インストーラ / 新 exe が再起動する
            }
        }
        catch (OperationCanceledException)
        {
            Post(new { type = "updateFailed", message = "更新を中止しました" });
        }
        catch (Exception ex)
        {
            Post(new { type = "updateFailed", message = ex.Message });
        }
        finally
        {
            _updateInProgress = false;
            _updateCts = null;
        }
    }

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
        _currentEncoding = encodingName.StartsWith("UTF-8 (", StringComparison.Ordinal) ? "UTF-8" : encodingName;
        _currentNewline = TextFile.DetectNewline(text);
        _dirty = false;
        _externalChangeNotified = false;
        UpdateTitle();
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

    // ------------------------------------------------------------------ new / save

    private void NewDocument()
    {
        _watcher?.Dispose();
        _watcher = null;
        _currentPath = null;
        _currentEncoding = "UTF-8";
        _currentNewline = "\r\n";
        _dirty = false;
        _externalChangeNotified = false;
        UpdateTitle();
        Post(new { type = "load", path = (string?)null, name = "無題.md", dir = (string?)null, text = "", encoding = "UTF-8", isNew = true, edit = true });
    }

    /// <summary>Web から受け取った本文を保存する。パス未定 (新規) または saveAs ならダイアログ。</summary>
    private void SaveDocument(string text, bool saveAs)
    {
        var target = _currentPath;
        if (saveAs || target == null)
        {
            using var dlg = new SaveFileDialog
            {
                Title = saveAs ? "名前を付けて保存" : "保存",
                Filter = "Markdown (*.md)|*.md|Markdown (*.markdown)|*.markdown|テキスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
                DefaultExt = "md",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = target != null ? Path.GetFileName(target) : "無題.md",
                InitialDirectory = target != null
                    ? Path.GetDirectoryName(target)
                    : (_settings.Recent.FirstOrDefault() is { } r && Directory.Exists(Path.GetDirectoryName(r)) ? Path.GetDirectoryName(r) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) { _afterSave = null; return; }
            target = dlg.FileName;
        }

        // 改行コードを元ファイルに合わせる (textarea は常に \n)
        var normalized = text.Replace("\r\n", "\n");
        if (_currentNewline == "\r\n") normalized = normalized.Replace("\n", "\r\n");

        try
        {
            _suppressWatchUntil = DateTime.UtcNow.AddSeconds(2);
            TextFile.Write(target, normalized, _currentEncoding);
        }
        catch (Exception ex)
        {
            _afterSave = null;
            MessageBox.Show(this, "保存に失敗しました。\n\n" + target + "\n" + ex.Message, "MD Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var pathChanged = !string.Equals(target, _currentPath, StringComparison.OrdinalIgnoreCase);
        _currentPath = target;
        _dirty = false;
        _externalChangeNotified = false;
        UpdateTitle();
        if (pathChanged)
        {
            var dir = Path.GetDirectoryName(target) ?? target;
            var core = _web.CoreWebView2;
            try { core.ClearVirtualHostNameToFolderMapping(DocHost); } catch { }
            core.SetVirtualHostNameToFolderMapping(DocHost, dir, CoreWebView2HostResourceAccessKind.DenyCors);
            WatchFile(target);
        }
        _settings.TouchRecent(target);
        _settings.Save();
        Post(new { type = "saved", path = target, name = Path.GetFileName(target), dir = Path.GetDirectoryName(target), encoding = _currentEncoding });
        Post(new { type = "recent", recent = _settings.Recent });
        Toast("保存しました: " + Path.GetFileName(target));

        var next = _afterSave;
        _afterSave = null;
        next?.Invoke();
    }

    /// <summary>
    /// 未保存の変更があれば「保存 / 破棄 / キャンセル」を確認してから next を実行する。
    /// 保存を選んだ場合は Web に本文を要求し、保存完了後に next を実行する。
    /// </summary>
    private void ConfirmDiscardThen(Action next, bool discardPrompt = false)
    {
        if (!_dirty) { next(); return; }
        if (discardPrompt)
        {
            var r0 = MessageBox.Show(this, "編集中の内容を破棄して読み直しますか？", "MD Viewer", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r0 == DialogResult.OK) { _dirty = false; next(); }
            return;
        }
        var name = _currentPath != null ? Path.GetFileName(_currentPath) : "無題.md";
        var r = MessageBox.Show(this, $"{name} への変更を保存しますか？", "MD Viewer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        switch (r)
        {
            case DialogResult.Yes:
                _afterSave = next;
                Post(new { type = "requestSave" });
                break;
            case DialogResult.No:
                _dirty = false;
                next();
                break;
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_dirty && e.CloseReason is CloseReason.UserClosing or CloseReason.None)
        {
            var name = _currentPath != null ? Path.GetFileName(_currentPath) : "無題.md";
            var r = MessageBox.Show(this, $"{name} への変更を保存しますか？", "MD Viewer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes)
            {
                e.Cancel = true;
                _afterSave = () => { _dirty = false; Close(); };
                Post(new { type = "requestSave" });
                return;
            }
            _dirty = false;
        }
        SaveWindowBounds();
    }

    private void UpdateTitle()
    {
        var name = _currentPath != null ? Path.GetFileName(_currentPath) : "無題.md";
        Text = (_dirty ? "*" : "") + name + " — MD Viewer";
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

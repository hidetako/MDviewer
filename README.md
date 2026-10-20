# MD Viewer — Windows 用スタンドアロン Markdown ビューア

`.md` ファイルを **人が読みやすい形で表示する** ための Windows アプリです。
Web アプリではなく、各 PC に `MdViewer.exe` を 1 つ置くだけで動きます (インストール不要・ネット接続不要)。

| 項目 | 内容 |
|---|---|
| 対応 OS | Windows 10 / 11 (x64) |
| 前提 | Microsoft Edge WebView2 ランタイム (Win10/11 には通常プリインストール済。無い場合は起動時に案内) |
| 配布形態 | 単一 exe (約 70 MB、.NET ランタイム同梱) |
| 技術 | .NET 8 WinForms + WebView2 / markdown-it + highlight.js + mermaid + DOMPurify |
| ライセンス | 同梱ライブラリのライセンスは `MdViewer/Assets/vendor/LICENSE.*` |

---

## 使い方

### 入手

1. GitHub の **Actions → "MD Viewer (Windows build)"** の最新実行から Artifact `MdViewer-win-x64` をダウンロード
   (または **Releases** の `md-viewer-vX.Y.Z` から `MdViewer.exe`)
2. 任意のフォルダ (例: `C:\Tools\MdViewer\`) に `MdViewer.exe` を置く
3. 初回起動時に Windows の SmartScreen 警告が出た場合は「詳細情報」→「実行」

### 開く

- `MdViewer.exe` を起動 → **開く** ボタン / `Ctrl+O`
- ウィンドウに `.md` ファイルを **ドラッグ&ドロップ**
- コマンドライン: `MdViewer.exe "C:\path\to\file.md"`
- `.md` を右クリック → **プログラムから開く** → `MdViewer.exe` を選択
  - 「⋯」メニュー → **.md の既定アプリに登録** を実行すると「プログラムから開く」候補に常に表示されます
    (Windows 10/11 の仕様で、最終的な既定アプリ指定はユーザ操作が必要。登録後に設定画面を開くか聞かれます)

### 画面と操作

| 操作 | キー | 説明 |
|---|---|---|
| 開く | `Ctrl+O` | ファイル選択ダイアログ |
| 再読込 | `F5` / `Ctrl+R` | ファイル保存を検知して **自動再読込** もします (スクロール位置保持) |
| 目次 | `Ctrl+B` | 左サイドバーに見出し一覧。クリックでジャンプ、現在位置をハイライト |
| テーマ | `Ctrl+D` | ライト / ダーク切替 (GitHub 風配色) |
| 文字サイズ | `Ctrl` + `+` / `-` / `0`、`Ctrl`+ホイール | 60%〜220% |
| 検索 | `Ctrl+F` | ページ内検索 (件数表示、`Enter` / `Shift+Enter` で移動) |
| 印刷 | `Ctrl+P` | Windows の印刷ダイアログ |
| PDF | ツールバー **PDF** | 表示中の文書を PDF に書き出し |
| 最近 | ツールバー **最近** | 直近 12 件の履歴 |

設定 (テーマ・文字サイズ・目次表示・履歴・ウィンドウ位置) は `%APPDATA%\MdViewer\settings.json` に保存されます。

### 対応している Markdown

- GitHub Flavored Markdown 相当: 見出し / リスト / **表** (横スクロール) / 引用 / 画像 / リンク / 取り消し線 / 自動リンク
- **コードブロックのシンタックスハイライト** (highlight.js 主要言語)
- **mermaid** 図 (` ```mermaid ` ブロック)
- タスクリスト `- [x]`、脚注 `[^1]`
- 先頭の YAML front matter (`---` で囲まれたメタ情報) は折りたたみ表示
- 文字コード: UTF-8 (BOM 有無) / UTF-16 / **Shift_JIS** を自動判定

リンクの挙動:

- `http(s)://` の外部リンク → **既定ブラウザ** で開く (ビューア内では遷移しない)
- 相対パスの `.md` リンク → ビューア内で開く (`#見出し` 指定も可)
- 相対パスの画像 → 文書と同じフォルダ基準で表示
- PDF / 画像 / Office 文書への相対リンク → 関連付けアプリで開く。実行ファイル等は安全のため開かない

---

## 開発

### 構成

```
tools/md-viewer/
├─ MdViewer/                 # C# (.NET 8 WinForms) ホスト
│  ├─ MdViewer.csproj
│  ├─ Program.cs             # エントリ。--register / --unregister CLI も処理
│  ├─ MainForm.cs            # WebView2 ホスト、ファイル I/O、ダイアログ、OS 連携
│  ├─ TextFile.cs            # 文字コード判定 (BOM → UTF-8 厳密 → Shift_JIS)
│  ├─ AppSettings.cs         # %APPDATA%\MdViewer\settings.json
│  ├─ AssetStore.cs          # 埋め込み Assets を %LOCALAPPDATA% へ展開
│  ├─ FileAssociation.cs     # HKCU への「プログラムから開く」登録
│  ├─ icon.ico
│  └─ Assets/                # exe に埋め込まれる UI
│     ├─ index.html / app.css / app.js   # 画面・操作
│     ├─ render.js                        # Markdown→HTML 純粋ロジック (Node テスト対象)
│     └─ vendor/                          # markdown-it, highlight.js, mermaid, DOMPurify, github-markdown-css
├─ tests/render.test.mjs     # node --test (依存なし)
├─ samples/sample.md         # 動作確認用
└─ README.md
```

ホスト (C#) と画面 (JS) は JSON メッセージでやり取りします (`app.js` 冒頭のコメント参照)。
UI のロジックは全て HTML/JS 側にあるので、見た目の調整は `Assets/` を編集するだけで済みます。

### ローカルビルド (Windows)

```powershell
dotnet publish tools/md-viewer/MdViewer/MdViewer.csproj -c Release -o publish
```

`publish\MdViewer.exe` が生成されます。Linux/macOS からも `EnableWindowsTargeting` を有効にしているため
`dotnet build` / `dotnet publish` までは通ります (実行は Windows のみ)。

### テスト

```bash
node --test tools/md-viewer/tests/render.test.mjs
```

### CI / リリース

- `.github/workflows/md-viewer-build.yml`
  - `tools/md-viewer/**` を触る PR / main への push で Node テスト → windows-latest で publish → Artifact `MdViewer-win-x64`
  - タグ `md-viewer-vX.Y.Z` を push すると **GitHub Release** を作成し `MdViewer.exe` と zip を添付
- バージョンは `MdViewer.csproj` の `<Version>` を更新してからタグを打つ

```powershell
git tag md-viewer-v0.1.0
git push origin md-viewer-v0.1.0
```

### 別リポジトリへの切り出し

このツールは本リポジトリ (ストレスチェック事業) とは独立しています。専用リポジトリに移す場合:

```powershell
git subtree split --prefix tools/md-viewer -b md-viewer-only
```

で履歴付きのブランチが得られます。その際は `.github/workflows/md-viewer-build.yml` のパス (`tools/md-viewer/` 部分) を修正してください。

---

## 既知の制限 / 今後の候補

- 数式 (KaTeX / MathJax) は未対応
- 複数ファイルのタブ表示は無し (ファイルごとに別ウィンドウで起動)
- exe は未署名のため、初回のみ SmartScreen 警告が出ることがあります
- WebView2 ランタイムが無い環境 (Windows Server のクリーン環境等) では Microsoft の配布ページからの導入が必要

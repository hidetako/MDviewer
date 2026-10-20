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

### 入手 (2 通り)

| 形態 | ファイル | 向き |
|---|---|---|
| **インストーラ版** | `MdViewer-Setup-X.Y.Z.exe` (Artifact `MdViewer-Setup`) | 普段使いの PC。スタートメニュー / デスクトップに登録、`.md` ダブルクリックで開く、「アプリと機能」からアンインストール可 |
| **ポータブル版** | `MdViewer.exe` (Artifact `MdViewer-win-x64`) | USB メモリや共有フォルダに置いて使う。インストール不要 |

どちらも GitHub の **Actions → "MD Viewer (Windows build)"** の最新実行ページ下部の **Artifacts**、
または **Releases** の `vX.Y.Z` からダウンロードできます。

**インストーラ版**

1. `MdViewer-Setup-X.Y.Z.exe` を実行 (SmartScreen 警告が出たら「詳細情報」→「実行」)
2. 「現在のユーザのみ」(管理者権限不要、`%LOCALAPPDATA%\Programs\MD Viewer`) か「すべてのユーザ」(`Program Files`) を選択
3. 「.md / .markdown ファイルを MD Viewer で開く」にチェックを入れると関連付けまで行います
   (他のアプリが既に既定になっている場合は上書きせず、「プログラムから開く」候補への追加のみ)
4. WebView2 ランタイムが無い PC では自動でインストールします (この時だけネット接続が必要)
5. アンインストールは Windows の「設定 → アプリ → インストールされているアプリ」から

**ポータブル版**

1. 任意のフォルダ (例: `C:\Tools\MdViewer\`) に `MdViewer.exe` を置く
2. 初回起動時に Windows の SmartScreen 警告が出た場合は「詳細情報」→「実行」

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
MDviewer/
├─ .github/workflows/build.yml  # CI: Node テスト → windows-latest で exe / インストーラ生成 → Artifact / Release
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
├─ installer/MdViewer.iss    # Inno Setup インストーラ定義 (CI の windows-latest でコンパイル)
├─ tests/render.test.mjs     # node --test (依存なし)
├─ samples/sample.md         # 動作確認用
└─ README.md
```

ホスト (C#) と画面 (JS) は JSON メッセージでやり取りします (`app.js` 冒頭のコメント参照)。
UI のロジックは全て HTML/JS 側にあるので、見た目の調整は `Assets/` を編集するだけで済みます。

### ローカルビルド (Windows)

```powershell
dotnet publish MdViewer/MdViewer.csproj -c Release -o publish
```

`publish\MdViewer.exe` が生成されます。Linux/macOS からも `EnableWindowsTargeting` を有効にしているため
`dotnet build` / `dotnet publish` までは通ります (実行は Windows のみ)。

### テスト

```bash
node --test tests/render.test.mjs
```

### CI / リリース

- `.github/workflows/build.yml`
  - PR / main への push で Node テスト → windows-latest で publish → Artifact `MdViewer-win-x64` (ポータブル exe) と `MdViewer-Setup` (インストーラ)
  - インストーラは Inno Setup 6 (`installer/MdViewer.iss`) で生成。ローカルで作る場合は Inno Setup を入れて
    `ISCC.exe /DSourceDir=publish /DAppVersion=0.1.0 installer\MdViewer.iss`
  - **Release の作り方 (3 通り、どれでも可)**
  1. `MdViewer/MdViewer.csproj` の `<Version>` を上げて main に push → 対応する `v<Version>` の Release が無ければ **自動作成** (通常はこれだけ)
  2. タグ `vX.Y.Z` を push
  3. Actions → "MD Viewer (Windows build)" → Run workflow → `release_tag` に `vX.Y.Z` を入力
- Release には インストーラ / `MdViewer.exe` / zip が添付される


---

## 既知の制限 / 今後の候補

- 数式 (KaTeX / MathJax) は未対応
- 複数ファイルのタブ表示は無し (ファイルごとに別ウィンドウで起動)
- exe は未署名のため、初回のみ SmartScreen 警告が出ることがあります
- WebView2 ランタイムが無い環境 (Windows Server のクリーン環境等) では Microsoft の配布ページからの導入が必要

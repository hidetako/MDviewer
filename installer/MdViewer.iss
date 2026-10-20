; MD Viewer — Inno Setup インストーラ定義
;
; ビルド (Windows / Inno Setup 6):
;   ISCC.exe /DSourceDir=<publish dir> /DAppVersion=0.1.0 ^
;            /DWebView2Bootstrapper=<MicrosoftEdgeWebView2Setup.exe のパス> tools\md-viewer\installer\MdViewer.iss
;
; 既定は「現在のユーザのみ」(管理者権限不要、%LOCALAPPDATA%\Programs\MD Viewer)。
; 起動時のダイアログで「すべてのユーザ」(Program Files) も選べる。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\installer-out"
#endif

#define MyAppName "MD Viewer"
#define MyAppPublisher "Yorozu Solutions"
#define MyAppExeName "MdViewer.exe"
#define MyProgId "MdViewer.Markdown"

[Setup]
AppId={{B7A4F0B2-5C7D-4E0B-9B9A-6D3E2F1A8C11}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=MdViewer-Setup-{#AppVersion}
SetupIconFile=..\MdViewer\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
ShowLanguageDialog=auto

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
japanese.AssocMd=.md / .markdown ファイルを {#MyAppName} で開く (関連付け)
english.AssocMd=Open .md / .markdown files with {#MyAppName}
japanese.InstallingWebView2=Microsoft Edge WebView2 ランタイムをインストールしています...
english.InstallingWebView2=Installing Microsoft Edge WebView2 Runtime...

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "associate"; Description: "{cm:AssocMd}"

[Files]
Source: "{#SourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
#ifdef WebView2Bootstrapper
Source: "{#WebView2Bootstrapper}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebView2Setup.exe"; Flags: deleteafterinstall; Check: not WebView2Installed
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; ProgId (HKA = 現在のユーザのみなら HKCU、全ユーザなら HKLM)
Root: HKA; Subkey: "Software\Classes\{#MyProgId}"; ValueType: string; ValueName: ""; ValueData: "Markdown 文書"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#MyProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Markdown 文書"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#MyProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#MyProgId}\shell\open"; ValueType: string; ValueName: ""; ValueData: "{#MyAppName} で開く"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#MyProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: associate
; 「プログラムから開く」一覧
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""; Tasks: associate
; 拡張子 → ProgId (既に他アプリが既定になっている場合は上書きしない)
; (既定値のアンインストール時削除は [Code] で「自分の ProgId のときだけ」行う)
Root: HKA; Subkey: "Software\Classes\.md"; ValueType: string; ValueName: ""; ValueData: "{#MyProgId}"; Flags: createvalueifdoesntexist; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.markdown"; ValueType: string; ValueName: ""; ValueData: "{#MyProgId}"; Flags: createvalueifdoesntexist; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate

[Run]
#ifdef WebView2Bootstrapper
Filename: "{tmp}\MicrosoftEdgeWebView2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "{cm:InstallingWebView2}"; Check: not WebView2Installed; Flags: waituntilterminated
#endif
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 展開済み Assets / WebView2 キャッシュ (設定 %APPDATA%\MdViewer\settings.json は残す)
Type: filesandordirs; Name: "{localappdata}\MdViewer"

[Code]
// WebView2 Evergreen ランタイムの有無を EdgeUpdate のレジストリで判定
function WebView2Installed: Boolean;
var
  Ver: String;
const
  ClientKey = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\' + ClientKey, 'pv', Ver) and (Ver <> '') and (Ver <> '0.0.0.0') then
    Result := True
  else if RegQueryStringValue(HKLM, 'SOFTWARE\' + ClientKey, 'pv', Ver) and (Ver <> '') and (Ver <> '0.0.0.0') then
    Result := True
  else if RegQueryStringValue(HKCU, 'Software\' + ClientKey, 'pv', Ver) and (Ver <> '') and (Ver <> '0.0.0.0') then
    Result := True;
end;

// 拡張子の既定値が自分の ProgId のときだけ消す (他アプリの関連付けは壊さない)
procedure RemoveDefaultIfOurs(const Ext: String);
var
  V: String;
begin
  if RegQueryStringValue(HKA, 'Software\Classes\' + Ext, '', V) and (V = '{#MyProgId}') then
    RegDeleteValue(HKA, 'Software\Classes\' + Ext, '');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveDefaultIfOurs('.md');
    RemoveDefaultIfOurs('.markdown');
  end;
end;

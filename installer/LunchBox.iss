; LunchBox インストーラ（Inno Setup 6）
; 先に publish.ps1 で dist\LunchBox.exe を作ってから、このファイルをコンパイルする

#define MyAppName "LunchBox"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Ciel Works"
#define MyAppURL "https://github.com/ciel-works/LunchBox"
#define MyAppExeName "LunchBox.exe"

[Setup]
AppId={{7C1E5B2A-4F3D-4E8B-9A61-2D5C8E0F4B17}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=LunchBox_Setup_{#MyAppVersion}
SetupIconFile=..\lunchbox.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにアイコンを作る"; GroupDescription: "追加の設定:"; Flags: unchecked

[Files]
Source: "..\dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} のアンインストール"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "LunchBox を起動する"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 常駐中なら終了させる
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillLunchBox"
; 「管理者権限で常駐」を使っていた場合の起動用タスクを消す（無ければ何もしない）
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""LunchBox"" /F"; Flags: runhidden; RunOnceId: "DeleteLunchBoxTask"

[Registry]
; スタートアップ登録（アプリの設定で付けたもの）をアンインストール時に消す
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "LunchBox"; Flags: uninsdeletevalue dontcreatekey

; 設定・履歴（%AppData%\LunchBox）は残す。完全に消したいときは手で削除する

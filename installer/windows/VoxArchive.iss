#define MyAppName "VoxArchive"
#define MyAppPublisher "VoxArchive Project"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

#ifndef SourceDir
  #error SourceDir is not defined. Use /DSourceDir=... when invoking ISCC.
#endif

#ifndef OutputDir
  #define OutputDir "..\..\output\installer"
#endif

#ifndef InstallerFlavor
  #define InstallerFlavor "fd"
#endif

[Setup]
AppId={{7E3F9A2C-B4D1-4F56-8C3E-9A0B2D4F6E1A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\VoxArchive
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=VoxArchive-setup-{#MyAppVersion}-{#InstallerFlavor}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupLogging=yes
UsePreviousAppDir=yes
DisableDirPage=no
UninstallDisplayIcon={app}\VoxArchive.Wpf.exe
AppMutex=VoxArchiveRunningMutex
CloseApplications=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english";  MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenu"; Description: "スタートメニューにショートカットを追加する"; GroupDescription: "追加オプション:"
Name: "startup";   Description: "Windows 起動時に自動起動する";               GroupDescription: "追加オプション:"; Flags: unchecked

[Icons]
Name: "{userprograms}\VoxArchive\VoxArchive"; Filename: "{app}\VoxArchive.Wpf.exe"; Tasks: startmenu
Name: "{userstartup}\VoxArchive";             Filename: "{app}\VoxArchive.Wpf.exe"; Tasks: startup

[Files]
; SourceDir には dotnet publish の出力フォルダを指定すること。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Run]
Filename: "{app}\VoxArchive.Wpf.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  PrereqPage: TWizardPage;

procedure InitializeWizard;
var
  InfoLabel: TNewStaticText;
  DotNetText: String;
begin
  PrereqPage := CreateCustomPage(
    wpWelcome,
    '事前インストールの確認',
    'VoxArchive を使用するために必要なソフトウェアを確認してください。'
  );

  InfoLabel := TNewStaticText.Create(PrereqPage);
  InfoLabel.Parent := PrereqPage.Surface;
  InfoLabel.Left := 0;
  InfoLabel.Top := 0;
  InfoLabel.Width := PrereqPage.SurfaceWidth;
  InfoLabel.Height := PrereqPage.SurfaceHeight;
  InfoLabel.AutoSize := False;
  InfoLabel.WordWrap := True;

  if CompareText('{#InstallerFlavor}', 'fd') = 0 then
  begin
    DotNetText :=
      '■ .NET 10 Desktop Runtime（必須）' + #13#10 +
      '    ランタイム非同梱版のため、事前インストールが必要です。' + #13#10 +
      '    まだインストールしていない場合は別途インストールしてください。';
  end
  else
  begin
    DotNetText :=
      '■ .NET Runtime（同梱）' + #13#10 +
      '    このインストーラーには実行に必要な .NET Runtime が含まれています。';
  end;

  InfoLabel.Caption :=
    DotNetText + #13#10 +
    '' + #13#10 +
    '■ ffmpeg（必須）' + #13#10 +
    '    FLAC エンコードに使用します。事前にインストールし、PATH が通っている' + #13#10 +
    '    必要があります。' + #13#10 +
    '    winget を使ってインストールできます:' + #13#10 +
    '        winget install Gyan.FFmpeg' + #13#10 +
    '' + #13#10 +
    '■ CUDA Toolkit 13.x（オプション）' + #13#10 +
    '    音声文字起こしで GPU アクセラレーションを使用する場合に必要です。' + #13#10 +
    '    GPU を使用しない場合はインストール不要です（CPU でも動作します）。';
end;

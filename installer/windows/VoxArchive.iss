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

#ifndef DotNetRuntimeMode
  #define DotNetRuntimeMode "CheckOnly"
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
UninstallDisplayIcon={app}\VoxArchive.exe
AppMutex=VoxArchiveRunningMutex
CloseApplications=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english";  MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenu"; Description: "スタートメニューにショートカットを追加する"; GroupDescription: "追加オプション:"
Name: "startup";   Description: "Windows 起動時に自動起動する";               GroupDescription: "追加オプション:"; Flags: unchecked

[Icons]
Name: "{userprograms}\VoxArchive\VoxArchive"; Filename: "{app}\VoxArchive.exe"; Tasks: startmenu
Name: "{userstartup}\VoxArchive";             Filename: "{app}\VoxArchive.exe"; Tasks: startup

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "precheck-dotnet.ps1"; Flags: dontcopy
Source: "{#SourceDir}\VoxArchive.runtimeconfig.json"; Flags: dontcopy

[Code]
var
  PrereqPage: TWizardPage;
  DotNetPrecheckErrorMessage: String;

procedure InitializeWizard;
var
  InfoLabel: TNewStaticText;
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
  InfoLabel.Caption :=
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

function RequiresDotNetPrecheck: Boolean;
begin
  Result := CompareText('{#DotNetRuntimeMode}', 'CheckOnly') = 0;
end;

function RunDotNetPrecheck: Boolean;
var
  ResultCode: Integer;
  ScriptPath: String;
  RuntimeConfigPath: String;
  LogPath: String;
  Args: String;
begin
  Result := True;
  DotNetPrecheckErrorMessage := '';

  if not RequiresDotNetPrecheck then
  begin
    Log('DotNet precheck skipped. DotNetRuntimeMode=' + '{#DotNetRuntimeMode}');
    exit;
  end;

  ExtractTemporaryFile('precheck-dotnet.ps1');
  ExtractTemporaryFile('VoxArchive.runtimeconfig.json');

  ScriptPath        := ExpandConstant('{tmp}\precheck-dotnet.ps1');
  RuntimeConfigPath := ExpandConstant('{tmp}\VoxArchive.runtimeconfig.json');
  LogPath           := ExpandConstant('{tmp}\voxarchive-dotnet-precheck.log');

  Args :=
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"' +
    ' -RuntimeConfigPath "' + RuntimeConfigPath + '"' +
    ' -LogPath "' + LogPath + '"';

  if not Exec('powershell.exe', Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    DotNetPrecheckErrorMessage :=
      '事前チェックの実行に失敗しました。PowerShell の実行権限を確認してください。';
    Result := False;
    exit;
  end;

  if ResultCode <> 0 then
  begin
    DotNetPrecheckErrorMessage :=
      '必要な .NET ランタイムが不足しているため、インストールを中断しました。' + #13#10 +
      '.NET 10 Desktop Runtime をインストールしてから再度お試しください。' + #13#10 +
      '詳細ログ: ' + LogPath + #13#10 +
      '終了コード: ' + IntToStr(ResultCode);
    Result := False;
    exit;
  end;

  Log('DotNet precheck passed.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not RunDotNetPrecheck then
    Result := DotNetPrecheckErrorMessage;
end;

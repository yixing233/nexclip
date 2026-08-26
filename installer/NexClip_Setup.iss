; NexClip Windows 正式安装程序 (轻量框架依赖版，自动检测并在线下载 .NET 9 与 Windows App SDK 依赖)
#define MyAppName "NexClip"
#define MyAppVersion "20260825.01"
#define MyAppPublisher "NexClip"
#define MyAppURL "https://github.com/yixing233/easy-clip"
#define MyAppExeName "NexClip.exe"
#define MySourceDir "..\bin\Release\publish-dependent"

[Setup]
AppId={{C47F6A29-8C2A-4C2E-98BF-7D8E6C7598F1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\bin\Release\Installer
OutputBaseFilename=NexClip_Setup_v{#MyAppVersion}_x64
SetupIconFile=..\Assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "开机自动启动 NexClip"; GroupDescription: "启动设置:"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;

function IsDotNet9Installed: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  // 检查 64 位 Program Files 下的 .NET 9 Desktop Runtime
  if FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), FindRec) then
  begin
    try
      Result := True;
    finally
      FindClose(FindRec);
    end;
  end;
  // 检查用户本地目录下的 .NET 9
  if not Result then
  begin
    if FindFirst(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), FindRec) then
    begin
      try
        Result := True;
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

function IsWinAppSdkInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  // 检查 Windows App SDK 运行时注册表与依赖项
  if RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\WindowsAppRuntime') or
     RegKeyExists(HKEY_CURRENT_USER, 'SOFTWARE\Microsoft\WindowsAppRuntime') or
     RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\Classes\Installer\Dependencies\Microsoft.WindowsAppRuntime.1.8') or
     RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\Classes\Installer\Dependencies\Microsoft.WindowsAppRuntime.1.7') or
     RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\Classes\Installer\Dependencies\Microsoft.WindowsAppRuntime.1.6') then
  begin
    Result := True;
  end;
  if not Result then
  begin
    if FindFirst(ExpandConstant('{commonpf}\WindowsApps\Microsoft.WindowsAppRuntime.1.*'), FindRec) then
    begin
      try
        Result := True;
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax <> 0 then
    Log(Format('  %d of %d bytes done.', [Progress, ProgressMax]))
  else
    Log(Format('  %d bytes done.', [Progress]));
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), '正在下载缺失的运行库依赖组件...', @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  NeedsDotNet: Boolean;
  NeedsWinAppSdk: Boolean;
  ResultCode: Integer;
begin
  Result := True;
  if CurPageID = wpReady then
  begin
    NeedsDotNet := not IsDotNet9Installed();
    NeedsWinAppSdk := not IsWinAppSdkInstalled();

    if NeedsDotNet or NeedsWinAppSdk then
    begin
      DownloadPage.Clear;
      if NeedsDotNet then
      begin
        DownloadPage.Add('https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe', 'dotnet9_desktop_runtime_x64.exe', '');
      end;
      if NeedsWinAppSdk then
      begin
        DownloadPage.Add('https://aka.ms/windowsappsdk/latest/windowsappruntimeinstall-x64.exe', 'windowsappruntimeinstall_x64.exe', '');
      end;

      DownloadPage.Show;
      try
        try
          DownloadPage.Download;
        except
          if DownloadPage.AbortedByUser then
            Log('用户取消了运行库下载')
          else
            SuppressibleMsgBox('下载运行库依赖失败，请检查网络连接后重试。' + #13#10 + GetExceptionMessage, mbError, MB_OK, IDOK);
          Result := False;
          Exit;
        end;

        // 安装 .NET 9 Desktop Runtime
        if NeedsDotNet then
        begin
          DownloadPage.SetText('正在安装 .NET 9 Desktop Runtime...', '');
          DownloadPage.SetProgress(0, 0);
          if not Exec(ExpandConstant('{tmp}\dotnet9_desktop_runtime_x64.exe'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
          begin
            Exec(ExpandConstant('{tmp}\dotnet9_desktop_runtime_x64.exe'), '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
          end;
        end;

        // 安装 Windows App SDK Runtime
        if NeedsWinAppSdk then
        begin
          DownloadPage.SetText('正在安装 Windows App SDK Runtime...', '');
          DownloadPage.SetProgress(0, 0);
          if not Exec(ExpandConstant('{tmp}\windowsappruntimeinstall_x64.exe'), '--quiet', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
          begin
            Exec(ExpandConstant('{tmp}\windowsappruntimeinstall_x64.exe'), '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
          end;
        end;

      finally
        DownloadPage.Hide;
      end;
    end;
  end;
end;

; ══════════════════════════════════════════════════════════════════════
;  ClassSoftwareHub 桌面版 — Inno Setup 6 安装脚本
;
;  编译（在工程根目录）：
;    & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ClassSoftwareHub.iss /DAppVersion=1.0.0 /DChannel=stable
;
;  产物：dist\installer\ClassSoftwareHub-Setup-dv<AppVersion>-<Channel>.exe
;
;  设计要点：
;   · 默认装到 %LOCALAPPDATA%\Programs\ClassSoftwareHub（免管理员），但**允许用户改路径**
;   · AppId 固定 → 升级/回滚都走「原地安装」，用户之前选的目录会被沿用
;   · 静默升级参数（更新器用的）：/SP- /SILENT /NORESTART /CLOSEAPPLICATIONS /TASKS="desktopicon"
;   · 用户数据在 %LOCALAPPDATA%\ClassSoftwareHub（设置/内容缓存/下载的更新包），跟程序目录分开，
;     卸载程序不会碰它
; ══════════════════════════════════════════════════════════════════════

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#ifndef Channel
  #define Channel "stable"
#endif

#define AppName "ClassSoftwareHub"
#define AppExeName "ClassSoftwareHub.exe"
#define AppSite "https://classsoftwarehub.us.ci"

[Setup]
; ⚠️ 这个 GUID 是「同一款软件」的标识：升级/回滚靠它认亲，**永远不要改**
AppId={{7A2C4D18-9F31-4C6B-8E5A-2D0B7F4A1C93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} dv{#AppVersion}
AppPublisher={#AppName}
AppPublisherURL={#AppSite}
AppSupportURL={#AppSite}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
PrivilegesRequired=lowest
OutputDir=..\dist\installer
OutputBaseFilename=ClassSoftwareHub-Setup-dv{#AppVersion}-{#Channel}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} dv{#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex={#AppName}.Desktop.SingleInstance
CloseApplications=yes
RestartApplications=no
; ⚠️ 故意留 6.1（Inno 允许的最低值）：系统版本我们自己用 [Code] 检查，这样能弹出「打开网页版」的按钮
; （Inno 自带的最低版本检查只会给一句冷冰冰的报错，没法加按钮）
MinVersion=6.1sp1

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; ══════════════════════════════════════════════════════════════════════
;  兜底逻辑（[Code]）
;   · 系统版本不够（低于 Win10 1809 / build 17763）→ 直接不给装，并给一个「打开网页版」的去处
;   · 系统够但缺 WebView2 运行时 → 给「自动下载安装」和「打开下载页」两条路，也可直接继续装
;     （主界面全是原生的，只有「提交软件」那个小窗口需要 WebView2，所以不拦着装）
;   · .NET 10 / Windows App SDK：我们是**自包含**发布（self-contained），不用另装，无需兜底
; ══════════════════════════════════════════════════════════════════════

[Code]

const
  WebUrl = '{#AppSite}';
  WebView2Page = 'https://developer.microsoft.com/microsoft-edge/webview2/#download-section';
  WebView2Bootstrapper = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  // WebView2 运行时在注册表里的固定 GUID
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

/// <summary>系统够不够跑 .NET 10 + WinUI3：要求 Windows 10 1809 及以上（build >= 17763）。</summary>
function SystemVersionOk(var Why: String): Boolean;
var
  V: TWindowsVersion;
begin
  GetWindowsVersionEx(V);
  Why := Format('Windows %d.%d (Build %d)', [V.Major, V.Minor, V.Build]);

  if V.Major < 10 then
  begin
    Result := False;
    Exit;
  end;

  if V.Build < 17763 then
  begin
    Result := False;
    Exit;
  end;

  Result := True;
end;

/// <summary>系统里有没有 Evergreen WebView2 运行时（按机器装 / 按用户装都认）。</summary>
function HasWebView2(): Boolean;
var
  Ver: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Ver) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Ver) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Ver);
end;

function OpenUrl(const Url: String): Boolean;
var
  Err: Integer;
begin
  Result := ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, Err);
end;

/// <summary>下载进度回调（这里不需要显示进度，直接放行）。</summary>
function OnWebView2Download(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

/// <summary>试着自动下载并静默安装 WebView2 运行时（装不成就退回去打开下载页）。</summary>
procedure TryInstallWebView2();
var
  TmpFile: String;
  ResultCode: Integer;
begin
  TmpFile := ExpandConstant('{tmp}\WebView2Setup.exe');
  try
    DownloadTemporaryFile(WebView2Bootstrapper, TmpFile, '', @OnWebView2Download);
    if Exec(TmpFile, '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, ResultCode)
       and (ResultCode = 0) then
    begin
      MsgBox('WebView2 运行时安装完成，接下来可以正常使用「提交软件」页面。', mbInformation, MB_OK);
      Exit;
    end;
    MsgBox('自动安装没成功（返回码 ' + IntToStr(ResultCode) + '），给你打开官方下载页，装好后重启本程序即可。',
           mbInformation, MB_OK);
  except
    MsgBox('没法自动下载 WebView2（可能没有网络）。给你打开官方下载页，装好后重启本程序即可。',
           mbInformation, MB_OK);
  end;
  OpenUrl(WebView2Page);
end;

function InitializeSetup(): Boolean;
var
  Why: String;
  Choice: Integer;
begin
  Result := True;

  // ── 1. 系统版本 ──────────────────────────────────────────────────
  if not SystemVersionOk(Why) then
  begin
    Choice := MsgBox(
      '这个应用跑不起来：它需要 Windows 10 1809（Build 17763）或更高版本。' + #13#10 +
      '你现在的系统是：' + Why + #13#10 + #13#10 +
      '不过别着急 —— 网页版在浏览器里（浏览器、手机都行）功能是一样的。' + #13#10 + #13#10 +
      '现在就打开网页版看看？',
      mbCriticalError, MB_YESNO);
    if Choice = IDYES then
      OpenUrl(WebUrl);
    Result := False;
    Exit;
  end;

  // ── 2. WebView2 运行时（可选组件）────────────────────────────────
  if not HasWebView2() then
  begin
    Choice := MsgBox(
      '没检测到 WebView2 运行时。' + #13#10 +
      '它不是必须的：主界面全是原生的，只有「提交软件」那个小窗口需要它。' + #13#10 + #13#10 +
      '要现在自动下载安装吗？（选「否」会先继续安装，之后随时可以在设置或提示里再装）',
      mbConfirmation, MB_YESNO);
    if Choice = IDYES then
      TryInstallWebView2();
  end;
end;

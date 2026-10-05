; ══════════════════════════════════════════════════════════════════════
;  ClassSoftwareHub 桌面版 — Inno Setup 6 安装脚本
;
;  编译（在工程根目录）：
;    & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ClassSoftwareHub.iss
;    （版本号默认取下面的 DesktopVersion；临时覆盖就加 /DDesktopVersion=1.1.0-insider1.5）
;
;  产物：dist\installer\ClassSoftwareHub-Setup-dv<DesktopVersion>.exe
;        内测例：ClassSoftwareHub-Setup-dv1.1.0-insider1.0.exe
;        正式例：ClassSoftwareHub-Setup-dv1.1.0.exe
;
;  版本号规则见 Core/ShellConfig.cs 里 ShellVersion 的注释（dv + 主.功能.补丁 + -insider架构.迭代）。
;  ⚠️ 更新器是按「文件名里含 setup」+ GitHub Release 的 tag 来认包的，
;     所以：① 文件名必须含 Setup；② 这里的 DesktopVersion 必须和发布时打的 tag 一字不差（tag 要带 dv 前缀）。
;
;  设计要点：
;   · 默认装到 %LOCALAPPDATA%\Programs\ClassSoftwareHub（免管理员），但**允许用户改路径**
;   · AppId 固定 → 升级/回滚都走「原地安装」，用户之前选的目录会被沿用
;   · 静默升级参数（更新器用的）：/SP- /SILENT /NORESTART /CLOSEAPPLICATIONS /TASKS="desktopicon"
;   · 用户数据在 %LOCALAPPDATA%\ClassSoftwareHub（设置/内容缓存/下载的更新包），跟程序目录分开，
;     卸载程序不会碰它
; ══════════════════════════════════════════════════════════════════════

; ⚠️ 唯一的版本号来源，必须和 Core/ShellConfig.cs 的 ShellVersion 一字不差（写在这里时**不带** dv 前缀）
#ifndef DesktopVersion
  #define DesktopVersion "1.1.1-insider1.0"
#endif

#define AppName "ClassSoftwareHub"
#define AppExeName "ClassSoftwareHub.exe"
#define AppSite "https://classsoftwarehub.us.ci"

[Setup]
; ⚠️ 这个 GUID 是「同一款软件」的标识：升级/回滚靠它认亲，**永远不要改**
AppId={{7A2C4D18-9F31-4C6B-8E5A-2D0B7F4A1C93}
AppName={#AppName}
AppVersion={#DesktopVersion}
AppVerName={#AppName} dv{#DesktopVersion}
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
OutputBaseFilename=ClassSoftwareHub-Setup-dv{#DesktopVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} dv{#DesktopVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; ⚠️⚠️ 这里**故意不设 AppMutex**（原值 `{#AppName}.Desktop.SingleInstance`）：
;     Inno 一启动就会查这个 Mutex，查到应用还在跑就直接拒装，而那个框又会被
;     /SUPPRESSMSGBOXES 自动按「取消」→ 静默失败、用户零提示。
;     旧版本客户端（≤ insider1.3）的更新流程本来就退不干净，正好被这一条卡死。
;     改由 [Code] PrepareToInstall 自己把旧应用关掉，见那段注释。
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
; 装完自动拉起应用（交互安装、以及应用内更新的静默安装都算）。
; 之前是 postinstall + skipifsilent -> 静默升级装完什么都不发生，用户以为应用崩了。
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait

; ══════════════════════════════════════════════════════════════════════
;  兜底逻辑（[Code]）
;   · 系统版本不够（低于 Win10 1809 / build 17763）→ 直接不给装，并给一个「打开网页版」的去处
;   · 系统够但缺 WebView2 运行时 → 给「自动下载安装」和「打开下载页」两条路，也可直接继续装
;     （界面全是原生的，只有应用内网页浮层用得到 WebView2，所以不拦着装）
;   · .NET 10 / Windows App SDK：我们是**自包含**发布（self-contained），不用另装，无需兜底
; ══════════════════════════════════════════════════════════════════════

[Code]

const
  WebUrl = '{#AppSite}';
  WebView2Page = 'https://developer.microsoft.com/microsoft-edge/webview2/#download-section';
  WebView2Bootstrapper = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  // WebView2 运行时在注册表里的固定 GUID
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

/// <summary>应用的单实例 Mutex 还在不在（在 = 旧版本还跑着）。</summary>
function AppIsRunning(): Boolean;
begin
  Result := CheckForMutexes('{#AppName}.Desktop.SingleInstance');
end;

/// <summary>
/// 安装前把还在运行的旧版本关掉。
///
/// ⚠️⚠️ 为什么非自己动手不可（2026-10-01 实测）：
///   ① 旧版本（≤ insider1.3）的更新流程调 `Application.Current.Exit()`，漏了应用自己的
///      「真要退出」标志 → 被托盘逻辑当成「用户点了 ×」→ 只把窗口藏起来，**进程不退**；
///   ② 这一段客户端代码**早就发到用户机器上了，改不了**。能改的只有这份安装脚本，
///      而它偏偏永远是「新」的（每次更新都从 GitHub 现拉）→ 所以只能在这儿兜底。
///   ③ 原先靠 `AppMutex` 拦（已从 [Setup] 移除）：Inno 启动 16 毫秒就查 Mutex，
///      查到应用还在 → 弹框 → 被 `/SUPPRESSMSGBOXES` 自动按「取消」→ 静默失败。
///   ④ 也不能指望 Inno 的 `/CLOSEAPPLICATIONS`：它走 Restart Manager，RM 关应用是发
///      **WM_CLOSE**，而本应用的 WM_CLOSE 被 close-to-tray 逻辑吞掉了（正是 ① 那个毛病）
///      → RM 永远关不掉它，这条参数对本项目等于没用。
///   ⑤ 所以只能 `taskkill`：先礼貌请一次（不带 `/F`），等一会儿还不退就强杀。
///      应用无未保存状态、设置即时落盘，被杀不丢数据；`.iss` 的 `[Run]` 段随后会把它拉起来。
/// </summary>
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R: Integer;
begin
  Result := '';
  if not AppIsRunning() then
    Exit;

  // 交互式安装时问一声；应用内更新的静默安装不打扰，直接关
  if not WizardSilent() then
  begin
    if MsgBox('检测到 {#AppName} 正在运行，安装前需要先关闭它。' + #13#10 + #13#10 +
              '是否继续？', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := '安装已取消：请先关闭 {#AppName} 再运行安装程序。';
      Exit;
    end;
  end;

  // ① 先好好请一次（用户若关掉了「关闭时收进托盘」，这一步它就自己退了）
  Exec('cmd.exe', '/c taskkill /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, R);
  Sleep(1500);

  // ② 还不退就强杀
  if AppIsRunning() then
  begin
    Exec('cmd.exe', '/c taskkill /F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(600);
  end;
end;

/// <summary>NT 内核号 → 用户认识的商品名（6.1 = Win7 这种对应关系别让用户自己翻译）。</summary>
function WindowsName(V: TWindowsVersion): String;
begin
  if (V.Major = 10) and (V.Build >= 22000) then
    Result := 'Windows 11'
  else if V.Major = 10 then
    Result := 'Windows 10'
  else if (V.Major = 6) and (V.Minor = 3) then
    Result := 'Windows 8.1'
  else if (V.Major = 6) and (V.Minor = 2) then
    Result := 'Windows 8'
  else if (V.Major = 6) and (V.Minor = 1) then
    Result := 'Windows 7'
  else if (V.Major = 6) and (V.Minor = 0) then
    Result := 'Windows Vista'
  else
    Result := Format('Windows %d.%d', [V.Major, V.Minor]);

  if V.ServicePackMajor > 0 then
    Result := Result + Format(' SP%d', [V.ServicePackMajor]);
end;

/// <summary>系统够不够跑 .NET 10 + WinUI3：要求 Windows 10 1809 及以上（build >= 17763）。</summary>
function SystemVersionOk(var Why: String): Boolean;
var
  V: TWindowsVersion;
begin
  GetWindowsVersionEx(V);
  Why := Format('%s（Build %d）', [WindowsName(V), V.Build]);

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
      '它不是必须的：界面全是原生实现，只有在应用内显示网页时才会用到（例如从软件详情页打开官网）。' + #13#10 + #13#10 +
      '要现在自动下载安装吗？（选「否」会先继续安装，之后随时可以在设置或提示里再装）',
      mbConfirmation, MB_YESNO);
    if Choice = IDYES then
      TryInstallWebView2();
  end;
end;

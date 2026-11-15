# ClassSoftwareHub Windows dv1.2.3 开发说明

## 基线与目标

- 基线：上游 tag `dv1.2.2`（commit `ed2c03fe276dad9ddea1a34110f81292a486fba0`）。
- 新版本：`dv1.2.3`。
- 目标框架：`net10.0-windows10.0.26100.0`。
- 最低平台：`10.0.17763.0`（Windows 10 1809）。
- UI：原生 WinUI 3 / Windows App SDK 2.5.1。

## 本轮落地的功能

### 1. 侧边栏固定快捷应用

- `Services/SettingsStore.cs`：新增 `PinnedApps`、`PinnedApp`（Name / Path / Arguments）。
- `Pages/ShellPage.xaml.cs`：
  - 动态生成“快捷应用”分组、“添加快捷方式…”和已固定条目。
  - `FileOpenPicker` 选择 `.exe` / `.lnk`。
  - `Process.Start(UseShellExecute = true)` 启动。
  - 右键菜单可从侧边栏移除。

### 2. 设置迁移向导

- `Services/ConfigMigration.cs`：导出 JSON 包（schema、app、version、exportedAt、settings）；导入时校验 schema，按字段合并 `AppSettings`（以当前对象为底，逐 key 覆盖），合并完成后一次性保存。
- 合并排除本机相关字段：窗口尺寸与位置、调色板位置、上次打开页面、截图 / 下载 / 计时铃声 / 公告音频路径、待安装更新标记、OOBE 状态、WebView2 选择；快捷键、侧边栏模块、缓存与日志保留数、AI 模板等偏好随包迁移。
- `Pages/MigrationWizardPage.xaml(.cs)`：导出 / 导入按钮与 `InfoBar` 结果提示。
- `Pages/ShellPage.xaml.cs`：新增 `settings-migration` 路由。
- 包格式错误统一包装为 `FormatException`，由向导页提示，不写入半成品配置。

### 3. 跨显示器 DPI / HDR 信息适配

- `Services/DisplayCapabilities.cs`：按窗口句柄读取 `GetDpiForWindow` 缩放、`MonitorFromWindow` + `GetMonitorInfo` 分辨率与设备名；HDR / 宽色域通过 `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo`（source name / advanced color）查询当前启用状态。
  WinUI 3 桌面进程没有 CoreWindow，`DisplayInformation.GetForCurrentView()` 会抛出 `GetForCurrentView must be called on a thread that is associated with a CoreWindow`，因此不采用该 API。
- `Pages/SettingsPage.xaml(.cs)`：外观分组新增「显示器适配」卡片，进入页面时刷新，显示缩放、分辨率与 SDR / HDR / 宽色域。
- `MainWindow.xaml.cs`：启动时与窗口位置变化时刷新采集结果。
- 自绘像素控件的跨屏重排仍未逐页核验。

### 4. Hub AI 公式 / 绘图联动与计算

- `Services/PlotBridge.cs`：AI 到函数图像页的一次性表达式投递通道。
- `Services/MathSolver.cs`：复用 `ExpressionParser`；中心差商求导、辛普森数值积分、扫描变号后二分求根。
- `Pages/FunctionPlotPage.xaml.cs`：打开时消费待绘表达式；分析栏追加 `f′(0)`、当前视口定积分、当前视口零点。
- `Pages/HubAiPage.xaml(.cs)`：新增“绘图”按钮；从输入框或最后一条 assistant 消息提取含 `x` 的候选表达式，本地编译校验通过后入队并导航到函数图像页。
- 说明：求导、积分、求根为数值近似，不是符号推导；切点根、重复根可能漏检。

### 5. AI 助手模板一键切换

- `Services/SettingsStore.cs`：新增 `HubAiTemplate`（`default` / `course` / `solver` / `coder`）。
- `Services/HubAi/AiChatService.cs`：`BuildSystemPrompt` 接收模板 Id；`TemplateSection` 给出课件助手、解题助手、代码助手预设。
- `Pages/SettingsPage.xaml(.cs)`：模板下拉选择并持久化。

### 6. Monster模式（2026-11-15 更名并扩展）

- `Services/SettingsStore.cs`：字段仍为 `MonsterMode`，界面统一称「Monster模式」。
- `Pages/SettingsPage.xaml(.cs)`：开关保存后通知主窗口；不再把「窗口背景效果」写成纯色。
- `MainWindow.xaml.cs`：`ApplyPerformanceMode` 统一处理渲染策略：
  - 主窗口强制纯色背景，不创建 Mica / DesktopAcrylic 控制器；
  - `Timeline.AllowDependentAnimations = false`，关闭依赖布局的动画；
  - 开启时挂起应用内网页浮层（`SuspendWebSheet`），释放 WebView2 占用；
  - 启动时按存档应用，不再需要进设置页点一次。
- `Services/BackdropHost.cs`：Monster 开启时所有经此上背景的工具窗、文档窗一律纯色。
- `Views/ToolSidebarWindow.xaml.cs`：开启时移除亚克力、面板改不透明；切换后立即刷新已存在的侧边栏。
- `Views/AnnounceWindow.xaml.cs`：开启时喊话窗不挂全屏亚克力，改用不透明深底。
- `Pages/ShellPage.xaml.cs`：构造时按存档移除切页过渡；关闭 Monster 后恢复 `NavigationThemeTransition + EntranceNavigationTransitionInfo`。
- 不修改系统电源计划、电池策略或 Windows「节电模式」。

### 7. 设置页「清除缓存」（2026-11-15 追加）

- `Pages/SettingsPage.xaml`：在「存储」分组里、紧跟「缓存保留数量」之后新增一张独立卡片
  **清除缓存 / 立即清除**（`&#xE894;` 图标）。
  - 该卡片独立成卡，不放入默认折叠的「本地缓存」`SettingsExpander`，避免入口不可见。
- `Services/CacheHome.cs`：新增 `CacheClearResult` 记录与 `ClearAll()`：
  - 删掉 `cache\` 下**除当前实例以外**的全部 `dv{版本}-{yyyyMMddHHmm}` 目录，返回（删除数、释放字节、失败数）；
  - 安全边界与 `Clean` 一致：只认 `^dv(.+)-(\d{12})$` 的目录、删前校验路径确实在 `cache\` 之下；
  - 当前实例目录永不删（里面的素材被 `BitmapImage` 持有，删必失败）；`Ensure()` 未初始化时整体拒绝动手；
  - 删不掉的（被占用）计入 `Failed` 并写 `cache` 日志，下次启动的 `CacheHome.Start()` 会再回收一轮。
- `Pages/SettingsPage.xaml.cs`：`ClearCache_Click` 先弹确认框（写明将删除几个目录、释放多少、保留当前实例），
  确认后 `Task.Run(CacheHome.ClearAll())` 在后台删、回 UI 线程 `RefreshCacheList()`，再弹结果框；
  另加 `ShowCacheMessageAsync` 单按钮提示。

### 8. 全局快捷键（2026-11-15 追加）

上课时无需切回主窗口即可触发喊话、计时与 PPT 触控。

| 组合键（默认） | 动作 |
| --- | --- |
| `Ctrl+Alt+0` | 把收进托盘的主窗口叫回来（`ShowFromTray`） |
| `Ctrl+Alt+1` | 跳到「教室喊话」（`Shell.NavigateTo("announce")`） |
| `Ctrl+Alt+2` | 弹出「课堂计时器」浮窗（`ToolPaletteWindow.ShowTool("timer")`）；不激活主窗口，避免遮挡正在放映的课件 |
| `Ctrl+Alt+3` | 跳到「PowerPoint 触控优化」（`Shell.NavigateTo("ppttouch")`） |

实现要点：

- `Services/HotkeyService.cs`：`RegisterHotKey(hwnd, …)` + comctl32 `SetWindowSubclass` 接 `WM_HOTKEY`。不另建隐藏窗口、不自行维护消息循环；子类化无需保管原窗口过程。
- 存档保存 `"Ctrl+Alt+1"` 形式的可读组合键，而非键码。解析器 `HotkeyCombo.TryParse` 认 Ctrl/Alt/Shift/Win + 字母/数字/F1~F24/方向与翻页等常用键。
- **注册失败必须如实上报**：设置页会把「已被其他程序占用」写到卡片状态行。`Ctrl+Alt+数字` 常被输入法、录屏与显卡面板占用。
- 录制新组合键时先 `Suspend()` 注销已注册热键；否则旧组合键会先触发功能，录制框收不到按键。
- 录制设 5 秒超时与失焦取消，避免误触写入存档。
- 设置页深链定位修正：`ActivateSection` 先停止「打开设置页后 200ms 回顶部」的定时器，否则 `csh://settings/hotkeys` 会停在外观分组。

## 版本号：1.2.3 → 1.2.3-Insider1.0（2026-11-15 追加）

- `Core/ShellConfig.cs`：`ShellVersion = "1.2.3-Insider1.0"`；
  `DisplayVersion` 由「把 `-insider` 替换成 `-Pr`」改为**原样返回** ——
  首页大标题、关于页、设置页「当前版本」、更新页必须显示同一个字面（用户要求首页照 `dv1.2.3-Insider1.0` 显示）。
  原先的 `-Pr` 显示别名停用；通道判定、更新 tag 比对、缓存目录命名本来只用 `ShellVersion`，不受影响。
- `ClassSoftwareHub.Desktop.csproj`：`Version` / `InformationalVersion` = `1.2.3-Insider1.0`；
  按仓库既定规则，`AssemblyVersion` / `FileVersion` 保持四段纯数字 `1.2.3.0`（insider1.0 → 末段 0）。
- `installer/ClassSoftwareHub.iss`：`DesktopVersion = "1.2.3-Insider1.0"`（安装包名会变成 `ClassSoftwareHub-Setup-dv1.2.3-Insider1.0.exe`）。
- 写法规定：`Insider` 的 **I 必须大写**（`1.2.3-Insider1.0`，不是 `insider`）。改版本号时四处一起改。

## Windows 编译步骤

在 Windows 10 1809+ 上装 .NET 10 SDK（10.0.100 及以上，工程无 `global.json` 锁定）：

```powershell
dotnet restore ClassSoftwareHub.Desktop.csproj
dotnet build ClassSoftwareHub.Desktop.csproj -c Debug
.\bin\Debug\net10.0-windows10.0.26100.0\win-x64\ClassSoftwareHub.exe
```

发布：

```powershell
dotnet publish ClassSoftwareHub.Desktop.csproj -c Release -r win-x64 -p:Platform=x64 `
  --self-contained true -p:PublishTrimmed=false -o dist\app
```

### 编译前必须先处理的一个环境坑

`%LOCALAPPDATA%\Temp\NuGetScratch` 若被**管理员权限进程**创建过，其 ACL 只给 SYSTEM / Administrators，
普通用户连读都被拒，`dotnet restore` 会报：

```
无法获取对 "...\Temp\NuGetScratch\lock" 的锁定文件访问权限
```

两种解法（任选其一）：

1. 管理员终端里删掉该目录，NuGet 会按 Temp 的继承权限重建：
   ```powershell
   takeown /F "%LOCALAPPDATA%\Temp\NuGetScratch" /R /D Y
   rmdir /S /Q "%LOCALAPPDATA%\Temp\NuGetScratch"
   ```
2. 不需要管理员：把本次构建的 TEMP 指到别处（已验证可用）：
   ```powershell
   $env:TEMP = "$env:LOCALAPPDATA\Temp\csh-nuget-tmp"; $env:TMP = $env:TEMP
   ```

## 当前验证状态（已在 Windows 真机编译通过）

**编译环境**：Windows，.NET SDK **10.0.404**（同机另有 8.0.428），Windows App SDK 2.5.1 自包含。

| 阶段 | 结果 |
| --- | --- |
| `dotnet restore` | 成功（首次 59.4 秒，增量 5.06 秒 / 3.41 秒） |
| `dotnet build -c Debug` | **成功，0 错误**；警告只有 1~2 条上游既有问题（`Pages/DetailPage.xaml.cs(386,76)` CS8604 可空实参） |
| 全量重建（`-t:Rebuild`） | **成功，0 错误**，4 分 14 秒（1.2.3 正式版那次） |
| 产物 | `ClassSoftwareHub.exe` 337 KB、`ClassSoftwareHub.dll` 10,542 KB、`ClassSoftwareHub.pri` 2,549 KB；输出目录合计 198.6 MB |
| 版本元数据 | FileVersion `1.2.3.0`，ProductVersion `1.2.3-Insider1.0`（insider 构建） |
| 启动冒烟 | 通过：进程稳定存活，主窗口标题 `ClassSoftwareHub`，未出现 `ms-appx:///MainWindow.xaml` 资源缺失闪退 |
| 首页版本字 | `PrintWindow` 截图核对：显示 `dv1.2.3-Insider1.0`，与 `Synesthesia` 同行、字号 28 粗体未变、未折行 |
| 全局快捷键 | 四个组合键在真机**实际按下**均触发（日志四条 `触发：…`）；窗口收进托盘（`vis=False`）后 `Ctrl+Alt+0` 能叫回来（按键后 `vis=True`） |
| 快捷键卡片 | `PrintWindow` 截图核对：展开后总开关、四个组合键按钮、恢复默认、状态行（「四个组合键都已生效。」）布局正常；默认保持收起 |
| 代码清理后构建 | **0 错误**，仍只有 2 条上游 CS8604；Debug 构建 39.44 秒（`build-clean5.log`） |
| 热键重构 | 实例改由 `SetWindowSubclass` 的 `refData` + `GCHandle` 传递后，四个组合键仍注册成功并逐条触发（日志四条 `已注册`、四条 `触发：…`） |
| 显示器适配卡片 | `PrintWindow` 截图核对：外观分组显示「缩放 125% · 1920×1080 · SDR」，读数与系统缩放一致；`[display]` 不再刷 CoreWindow 异常 |

真机编译阶段修复的问题：

- `Pages/HubAiPage.xaml.cs` 三处正则用了 C# 不认的转义（`\-`、`\s`）导致 CS1009；已全部改成逐字字符串 `@"..."`（`@"\r?\n|```"`、`@"[0-9a-zA-Zsinco^/()+\\-*]"`、`@"^y\s*=\s*"`）。
- 由此连带的两条 `WMC0001 Unknown type 'NullToCollapsed'/'NotNullToCollapsed'` 与 `WMC9999` 是 C# 编译失败导致 XAML 第二遍拿不到本地程序集的连锁报错，修复 C# 后自动消失。

更早一轮静态排查修复的问题：

- `DisplayCapabilities` 原先调用的 `DisplayMonitor.GetForDisplayId` / `NativeScaleFactor` / `SizeInPixels` 在 .NET 10 + Windows App SDK 投影中不存在；`DisplayInformation` 也没有可用的 `CreateForWindowId`，`GetForCurrentView()` 在 WinUI 3 桌面进程抛 CoreWindow 异常，最终改为纯 Win32 显示配置查询。
- 该改动首次编译时 `path.target.targetAvailable` 按 `bool` 使用导致 CS0023（该字段是 `int`），已改为 `== 0` 判断。
- `MainWindow.ApplyPerformanceMode` 中 `ShellPage` 未加 `Pages.` 前缀（该文件未 using Pages）；已修正。
- `MathSolver` 缺少 `using System.Linq;`（`roots.Any`）；已补齐。
- 函数页数值分析处对可空委托补了 null 宽容注解。

**仍未做的验收**：

- 「清除缓存」卡片与两个确认 / 结果对话框仍未在 UI 上点验；
- 迁移向导的导出 / 导入完整交互未点验（字段级合并只做了编译与逻辑审查）；AI 模板热切换未接真实模型验证；
- 七项新功能的逐页交互（侧边快捷、跨屏 DPI、AI 绘图联动、模板切换、Monster）仍未逐项点击；
- 未执行 Release 发布与 Inno Setup 打包；未在 Windows 10 1809 实机（更低系统版本）上验证。
- 本轮 Monster模式扩展（纯色背景、侧边栏/喊话窗降载、动画策略）改动时 Windows 设备离线，**尚未真机编译与运行验证**；设备恢复后需补 `dotnet build` 并核对 `[monster]` 日志、背景与侧边栏实际效果。

## 已知限制

- 固定快捷方式目前只存 Name / Path / Arguments，未提取 `.exe` / `.lnk` 自定义图标。
- 迁移向导按字段合并 `AppSettings`，窗口几何、显示器布局、文件路径与 OOBE 状态等本机字段不迁移；激活态、设备指纹、缓存文件不随包迁移。
- `DisplayCapabilities` 只采集窗口所在显示器；HDR / 宽色域分支在非 HDR 真机上无法触发，未做实机验证。跨显示器拖动后自绘像素控件的完整 DPI 重排尚未逐页核验。
- Monster模式关闭背景材质、切页过渡与依赖布局动画；已打开且未重建的工具浮窗不会立即换背景，重开生效。系统级电源与电池策略不受影响。
- AI 模板已持久化并拼入 system prompt；尚未提供模板内容预览或自定义提示词编辑界面。
- 「清除缓存」只删旧版本素材目录；当前实例目录因文件被界面持有而保留，需要重启后再清一次才能清空。
- 版本号改成 `1.2.3-Insider1.0` 后，缓存目录名前缀变为 `dv1.2.3-Insider1.0-`，旧的 `dv1.2.3-*` 目录会当作旧版本被回收。
- 全局快捷键只支持**带修饰键**的组合（单键会抢走全系统打字，一律不受理）；冲突时该功能按不出来，只在设置页状态行提示。
- 快捷键录制按「字母/数字/F1~F24/方向与翻页等」为限，标点符号键暂不支持。

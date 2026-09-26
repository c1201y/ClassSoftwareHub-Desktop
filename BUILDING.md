# 自己编译 / 发版

这份是给要改代码、自己打包的人看的。只是想用的话看 [README.md](README.md) 就行。

## 编译环境

Visual Studio 2022 或更新（要 .NET 10 SDK + Windows App SDK 组件）。

```powershell
# 调试构建
dotnet build ClassSoftwareHub.Desktop.csproj -c Debug

# 运行（exe 在 bin\Debug\net10.0-windows10.0.26100.0\win-x64\）
.\bin\Debug\net10.0-windows10.0.26100.0\win-x64\ClassSoftwareHub.exe
```

## 打安装包

```powershell
# 1. 发布到 dist\app
dotnet publish ClassSoftwareHub.Desktop.csproj -c Release -r win-x64 -p:Platform=x64 `
  --self-contained true -p:PublishTrimmed=false -o dist\app

# 2. 用 Inno Setup 6 编译脚本（ISCC 装在 %LOCALAPPDATA%\Programs\Inno Setup 6\）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ClassSoftwareHub.iss
```

版本号默认取 `.iss` 里的 `DesktopVersion`，不用在命令行传。临时想改就加 `/DDesktopVersion=1.1.0-insider1.1`。

产物在 `dist\installer\ClassSoftwareHub-Setup-dv<DesktopVersion>.exe`。`.iss` 里的 `AppId` 是升级和回滚认亲用的，不要改。

> ⚠️ `.iss` 的 `DesktopVersion` 必须和 `Core/ShellConfig.cs` 的 `ShellVersion` 一字不差、
> 也必须和发布时打的 git tag 一字不差（tag 带 `dv` 前缀，`.iss` 里不带）。三处不一致 → 更新器认不出新版本。

## 版本号规则

格式：**`dv` + `主.功能.补丁` + `-insider架构.迭代`**

| 段 | 含义 |
| --- | --- |
| `主` | 架构代次。只有整个应用的架构/技术路线发生重大变动才动 |
| `功能` | 每叠加一块新功能涨一次 |
| `补丁` | 小功能推送 / 小更新 / 小 bug 修复 |
| `insider架构` | 预览线自己的架构/思路基线，性质同主版本第一位 |
| `insider迭代` | 这条预览线上的具体更改次数 |

递增流程：做出一个能用的版本 → 发 `1.1.0-insider1.0` → 用户反馈还有问题 → 继续改成 `1.1.0-insider1.1`
→ 一直改到没问题 → **整个 `-insider` 后缀删掉** → 上线正式版 `1.1.0`。

要改版本时，**四处一起改**，缺一处就会出现"装上去还是旧版本号"：

1. `Core/ShellConfig.cs` → `ShellVersion`
2. `ClassSoftwareHub.Desktop.csproj` → `Version`、`InformationalVersion`
   （`AssemblyVersion` / `FileVersion` 只接受四段纯数字，塞不进 `-insider`，固定 `1.1.0.0` 即可）
3. `installer/ClassSoftwareHub.iss` → `DesktopVersion`
4. 发版时的 git tag

## 发版

```powershell
$env:GITHUB_TOKEN = "..."   # 需要仓库写权限，别写进任何文件
node --use-system-ca tools\publish-release.mjs `
  --tag dv1.1.0-insider1.0 --channel insider `
  --installer "dist\installer\ClassSoftwareHub-Setup-dv1.1.0-insider1.0.exe" `
  --name "ClassSoftwareHub dv1.1.0-insider1.0" --notes notes.md
```

脚本会算 MD5/SHA256、生成同名 `.md5`、建 Release 并把安装包传上去。tag 里带 `insider` 就自动标预发布（只有 Insider 通道的客户端会收到），正式版用 `dv1.1.0` 这种。

## 目录

```
Core/        配置、路径、内容读取、几个共用小工具
Data/        软件、工具、镜像站这类数据模型
Services/    设置存储、内容同步、更新（GitHub Releases）、嵌入资源
Pages/       首页 / 软件下载 / 详情 / 内置工具 / 提交 / 设置
  Tools/     六个工具页
Views/       独立小窗口
Web/         注入脚本
installer/   Inno Setup 脚本
tools/       发版脚本
```

## 内容包（软件清单的来源）

客户端的软件清单不是写死在代码里的，来自「内容包」：`apps/*.json`（一个软件一个）+ `categories.json`
+ `text/*.json`（界面文案、镜像站清单）+ `manifest.json`（版本号）。

取数顺序（`Services/GithubContentSync.cs` → `Services/ContentUpdater.cs`）：

1. **首选：直接读站点仓库** `c1201y/ClassSoftwareHub`（公开仓库，不用令牌）
   - 先用 GitHub 接口问一下分支头 sha（未登录 60 次/小时，机房是同一个出口 IP，所以**只问这 1 个请求**）
   - sha 没变就收工；变了才列出 `软件数据/` 下的 json 再逐个取内容（走 CDN，不吃配额）
   - 落地到 `%LOCALAPPDATA%\ClassSoftwareHub\content`（就是 ContentStore 优先读的缓存目录）
   - ⚠️ 取文件的入口按顺序回退：`raw.githubusercontent.com` → `cdn.jsdelivr.net` → `fastly.jsdelivr.net`
     → `gh-proxy.com`；接口入口：`api.github.com` → `gh-proxy.com` → `ghfast.top`。
     成功的入口记在 `%LOCALAPPDATA%\ClassSoftwareHub\content-base.txt`，下次优先用。
     （国内/校园网里 raw 经常不通，回退是必需品，不是保险）
2. 备胎：站点的 `content/manifest.json`（按 sha256 增量拉。⚠️ **站点目前还没发布这个文件**）
3. 兜底：**安装包自带的内容包**（见下）
4. 开发兜底：站点工程的 `dist/content`（Debug 构建优先它，改完站点不用等发布就能看效果）

读取优先级（`Core/ContentStore.cs`）：Debug = 开发目录 → 缓存 → 自带；Release = 缓存 → 自带 → 开发。

### 打包时把内容包塞进去（装机就有清单，离线也不空）

```powershell
dotnet publish ClassSoftwareHub.Desktop.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:PublishTrimmed=false -o dist\app
node tools\sync-content.mjs      # 把站点 dist/content 拷进 dist\app\content（80 个文件 / 约 450KB）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ClassSoftwareHub.iss
```

`installer\ClassSoftwareHub.iss` 的 `[Files]` 是 `Source: "..\dist\app\*"` 整目录，所以 `dist\app\content`
会自动进安装包。装完后 `{app}\content` 就是自带内容包；GitHub 同步会顺手把里面缺的 `text/*.json` 补进缓存目录。

> 内容包从哪来：站点仓库跑 `node scripts/build-content.mjs`（产物在站点工程的 `dist/content`）。

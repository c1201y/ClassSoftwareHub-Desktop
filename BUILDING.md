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

版本号默认取 `.iss` 里的 `DesktopVersion`，不用在命令行传。临时想改就加 `/DDesktopVersion=1.1.0-insider1.3`。

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

递增流程：做出一个能用的版本 → 发 `1.1.0-insider1.0` → 用户反馈还有问题 → 继续改成 `1.1.0-insider1.1`、`1.1.0-insider1.2`
→ 一直改到没问题 → **整个 `-insider` 后缀删掉** → 上线正式版 `1.1.0`。

要改版本时，**四处一起改**，缺一处就会出现"装上去还是旧版本号"：

1. `Core/ShellConfig.cs` → `ShellVersion`
2. `ClassSoftwareHub.Desktop.csproj` → `Version`、`InformationalVersion`
   （`AssemblyVersion` / `FileVersion` 只接受四段纯数字，塞不进 `-insider`，固定 `1.1.0.0` 即可）
3. `installer/ClassSoftwareHub.iss` → `DesktopVersion`
4. 发版时的 git tag

## 发版

**顺序：先 `git push` main → 再提交一次并打 tag → 最后跑脚本发 Release。**
（tag 要指向已经推上去的提交，而且 Release 正文首行的配图链接指向本 tag 的资产，资产传完才显示得出来。）

```powershell
$env:GITHUB_TOKEN = "..."   # 需要仓库写权限，别写进任何文件
node --use-system-ca tools\publish-release.mjs `
  --tag dv1.1.0-insider1.3 --channel insider `
  --installer "dist\installer\ClassSoftwareHub-Setup-dv1.1.0-insider1.3.exe" `
  --image "dist\release\dv1.1.png" `
  --name "ClassSoftwareHub dv1.1.0-insider1.3" --notes "dist\release\notes.md"
```

不确定参数对不对、或者想先看一眼要发什么，加 `--dry-run`：只打印摘要 + 算哈希 + 写 `.md5`，不碰 GitHub。

脚本会算 MD5/SHA256、生成同名 `.md5`、建 Release，并把**安装包 + 同名 `.md5` + 配图 png** 一起传上去。
tag 里带 `insider` 就自动标预发布（只有 Insider 通道的客户端会收到），正式版用 `dv1.1.0` 这种。

> ⚠️ 配图（Release 正文顶部那张 `![](...)`）**必须用 `--image` 传上去**，否则正文会挂一张空图。
> 以前这步是手工补的，现在跟着一起走（2026-09-30）。

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

## 软件清单（唯一来源：网络）

⛔⛔ **安装包不自带任何内容**（2026-10-05 Nick 定案「以后统统只有通过网络获取更新的途径」）。
客户端的软件清单不是写死在代码里的，也不随安装包发布，只能联网从站点仓库读。

取数顺序（`Services/GithubContentSync.cs` → `Services/ContentUpdater.cs`）：

1. **首选：直接读站点仓库** `c1201y/ClassSoftwareHub`（公开仓库，不用令牌）
   - 先用 GitHub 接口问一下分支头 sha（未登录 60 次/小时，机房是同一个出口 IP，所以**只问这 1 个请求**）
   - sha 没变就收工；变了才列出 `软件数据/` 下的 json 再逐个取内容（走 CDN，不吃配额）
   - 落地到 `%LOCALAPPDATA%\ClassSoftwareHub\content`（就是 ContentStore 读的那个目录）
   - ⚠️ 取文件的入口按顺序回退：`raw.githubusercontent.com` → `cdn.jsdelivr.net` → `fastly.jsdelivr.net`
     → `gh-proxy.com`；接口入口：`api.github.com` → `gh-proxy.com` → `ghfast.top`。
     成功的入口记在 `%LOCALAPPDATA%\ClassSoftwareHub\content-base.txt`，下次优先用。
     （国内/校园网里 raw 经常不通，回退是必需品，不是保险）
   - 仓库里没有的文件**本地也会删掉**（下架的软件不能留在客户端里）。
     ⚠️ GitHub 的 tree 接口返回 `truncated:true` 时跳过删除 —— 那种情况下清单是残缺的，删了等于误伤。
2. 备胎：站点的 `content/manifest.json`（按 sha256 增量拉。⚠️ **站点目前还没发布这个文件**，这条是死路）
3. 开发兜底：站点工程的产物目录（`ShellConfig.DevContentDir`）。只有本机开发会命中，
   正式用户机器上那个路径不存在。

读取优先级（`Core/ContentStore.cs`）：Debug = 开发目录 → 缓存；Release = 缓存 → 开发。
**没有"安装包自带"这一档了。**

### 这条变化带来的硬约束

- **新装的机器第一次启动必须联网**，否则清单是空的。所以软件下载页有专门的空状态
  （「正在获取软件清单」/ 失败给重试按钮），并且 `ContentStore.Changed` 事件负责在数据到位后
  自动刷新界面 —— 别删那个通知，否则用户得手动切页才能看到列表。
- ⚠️ 2026-10-09 起客户端**不再读** `软件数据/text/mirror-sites.json` ——
  那份文件是给已下线的「系统镜像下载」页用的（`ContentStore.LoadMirror` 连同页面一起删除）。

### 打包

```powershell
dotnet publish ClassSoftwareHub.Desktop.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:PublishTrimmed=false -o dist\app
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ClassSoftwareHub.iss
```

> ⚠️ `dotnet publish -o dist\app` 是**覆盖式写入、不清理目录**。如果 `dist\app\content` 里还留着
> 早期版本塞进去的内容包（2026-10-05 之前的产物），**发布前删掉它**：
>
> ```powershell
> Remove-Item -Recurse -Force dist\app\content -ErrorAction SilentlyContinue
> ```
>
> **务必放在 `dotnet publish` 之前** —— `csproj` 里的 `VerifyNoBundledContent` 是道硬闸，
> 发布产物里只要还有 `content` 就**直接让 publish 报错**（2026-10-05 起）。
> 这道闸是"宁可挡住发布，也不让内容包发出去"，所以要清就得先清。
> 忘了清的话，安装包会白胖 450KB 左右、并让装机首启看起来"有清单" —— 掩盖真正的联网路径有没有通。

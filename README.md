# 班级软件中心 · 桌面版

班级软件中心（[classsoftwarehub.us.ci](https://classsoftwarehub.us.ci)）的 Windows 客户端，WinUI 3 原生写的。

网页版浏览器里就能开，这是个装在电脑上的版本：启动快、界面是原生控件、能记住你上回看到哪儿，还能跟着系统一起开机启动。适合机房里那台天天开着的电脑，也适合自己电脑上装一个省得每次翻收藏夹。

## 能干什么

- **首页** —— 打个招呼、显示当前版本和几个快捷入口（项目仓库、作者主页、Q 群之类的）
- **软件下载** —— 搜索 + 分类筛选，点进详情页能看简介、版本、体积、系统要求，多个下载源可选，带校验值
- **内置工具** —— 图片取色、随机抽号、课堂计时器、全屏时钟、编码/哈希、系统镜像下载（不用为了抽个号专门打开浏览器）
- **提交软件** —— 直接填表提交到审核队列；也可以丢个 GitHub 仓库地址进去，自动读仓库的简介、版本号和安装包直链，省得一个个手抄
- **设置** —— 外观（颜色模式、背景效果）、窗口（置顶、开机自启、最小化启动）、更新通道、版本记录

## 下载

去 [Releases](../../releases) 下 `ClassSoftwareHub-Setup-dv*.exe`，双击装。

- 装到 `%LOCALAPPDATA%\Programs\ClassSoftwareHub`，**不用管理员权限**，安装目录可以自己改
- **自包含**：不用另装 .NET 运行时，也不用装 Windows App SDK
- 设置、内容缓存、下载的更新包都在 `%LOCALAPPDATA%\ClassSoftwareHub`，跟程序目录分开，卸载不会碰它
- 环境要求：Windows 10 1809（17763）或更高，x64

**两个更新通道**：

| 通道 | 拿到的版本 |
| --- | --- |
| 稳定通道 | 只有正式发布版，相对稳 |
| Insider 通道 | 连预发布版一起拿，新功能先到，遇到 bug 算你倒霉 |

设置里随时能切，切的只是「以后检查更新看哪条线」，已经装上的版本不会自己跳。想装回旧版本，在设置 → 更新里展开「从仓库加载可回滚的版本」。

## 自己编译

要 Visual Studio 2022 或更高（带 .NET 10 SDK + Windows App SDK 组件）。命令行：

```powershell
# 调试运行（第一次会自动还原 NuGet）
dotnet build ClassSoftwareHub.Desktop.csproj -c Debug

# 跑起来（生成的 exe 在 bin\Debug\net10.0-windows10.0.26100.0\win-x64\）
.\bin\Debug\net10.0-windows10.0.26100.0\win-x64\ClassSoftwareHub.exe
```

打包安装包（先发布，再用 Inno Setup 6 编译脚本）：

```powershell
dotnet publish ClassSoftwareHub.Desktop.csproj -c Release -r win-x64 -p:Platform=x64 `
  --self-contained true -p:PublishTrimmed=false -o dist\app

& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ClassSoftwareHub.iss `
  /DAppVersion=1.2.0 /DChannel=insider
```

产物在 `dist\installer\`。`AppId` 是写死在 `.iss` 里的（升级和回滚靠它认亲），别动。

## 目录

```
Core/        配置、路径、内容读取、几个共用小工具
Data/        软件、工具、镜像站这类数据模型
Services/    设置存储、内容同步、更新（GitHub Releases）、嵌入资源
Pages/       首页 / 软件下载 / 详情 / 内置工具 / 提交 / 设置
  Tools/     六个工具页
Views/       独立小窗口
Web/         注入脚本（网页版那套外壳用得上）
installer/   Inno Setup 脚本
tools/       发版脚本
```

## 内容从哪来

软件列表、分类、文案都不是写死在客户端里的。站点仓库构建时会生成一份内容包（`manifest.json` + `text/*.json` + `apps/*.json`），客户端启动时同步到本地缓存，所以**站点那边加了新软件，这边打开就能看到**，不用重新装客户端。

## 发版

```powershell
# 需要一枚有仓库权限的令牌，只在当前终端里给
$env:GITHUB_TOKEN = "..."   # 别写进任何文件
node tools\publish-release.mjs --tag dv1.2.0-insider1.2 --channel insider
```

脚本会算好 MD5 / SHA256、生成 `.md5`、建 Release 并把安装包传上去。tag 带 `-insider` 就自动标成预发布，稳定版和 Insider 通道的客户端各取各的。

## 说明

- 界面是 WinUI 3 默认样式，没有额外美化；深浅色跟着系统走
- 只做 Windows，不做跨平台
- 用着有问题、或者想加点什么，直接开 Issue 说

## 谢一下

- 网站和软件数据来自 [班级软件中心](https://classsoftwarehub.us.ci)，感谢所有往上面提交过软件的同学们
- 界面框架 WinUI 3 / Windows App SDK，图标字体来自微软 Segoe Fluent Icons
- 安装包用 Inno Setup 6 打

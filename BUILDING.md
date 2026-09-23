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
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ClassSoftwareHub.iss `
  /DAppVersion=1.2.0 /DChannel=insider
```

产物在 `dist\installer\`。`.iss` 里的 `AppId` 是升级和回滚认亲用的，不要改。

## 发版

```powershell
$env:GITHUB_TOKEN = "..."   # 需要仓库写权限，别写进任何文件
node --use-system-ca tools\publish-release.mjs `
  --tag dv1.2.0-insider1.2 --channel insider `
  --installer "dist\installer\ClassSoftwareHub-Setup-dv1.2.0-insider.exe" `
  --name "ClassSoftwareHub dv1.2.0-insider1.2" --notes notes.md
```

脚本会算 MD5/SHA256、生成同名 `.md5`、建 Release 并把安装包传上去。tag 里带 `insider` 就自动标预发布（只有 Insider 通道的客户端会收到），正式版用 `dv1.2.0` 这种。

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

## 内容包

软件列表、分类、文案都不在客户端里硬编码。站点仓库构建时会生成内容包（`manifest.json` + `text/*.json` + `apps/*.json`），客户端启动时同步到本地缓存，站点那边加了新软件，这边打开就能看到。

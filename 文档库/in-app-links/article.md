# 文档里的应用内跳转链接

文档库的正文支持两类链接。一类指向网页，点开由浏览器打开；另一类指向应用内部的位置，以 `csh://` 开头，点开后直接落到相应页面、工具或设置分组，不必先回到导航栏逐项查找。本文列出当前版本可用的全部地址，投稿时按此填写即可。

## 地址格式

| 写法 | 打开的内容 | 示例 |
| --- | --- | --- |
| `csh://page/<标识>` | 应用内的某个页面 | `csh://page/downloads` |
| `csh://tool/<标识>` | 某个内置工具 | `csh://tool/disk-map` |
| `csh://settings/<分组>` | 设置页的某一组 | `csh://settings/update` |
| `csh://article/<文章标识>` | 文档库中的另一篇文章 | `csh://article/c-drive-cleanup` |

标识由小写字母、数字与连字符组成。地址写在 Markdown 链接的圆括号内，链接文字写页面名称：

```markdown
清理完成后，可在[任务进行](csh://page/downloads)页面查看结果。
```

## 页面一览

| 标识 | 页面 | 说明 |
| --- | --- | --- |
| `home` | 首页 | 启动后的默认页面 |
| `apps` | 软件下载 | 软件清单与详情 |
| `submit` | 提交软件 | 投稿表单，入口在软件下载页与首页 |
| `downloads` | 任务进行 | 下载与清理的进度 |
| `docs` | 文档库 | 本页所在的位置 |
| `dictionary` | 查阅单词 | 中英文查词与整句翻译 |
| `announce` | 教室喊话 | 向同一配对码的设备发送通知 |
| `sidebar` | 侧边布局 | 侧边栏模块的勾选与排序 |
| `tools-classroom` | 课堂工具 | 工具分组页 |
| `tools-system` | 系统工具 | 工具分组页 |
| `experimental` | 实验性功能 | 尚未确定上线的功能 |
| `machinecheck` | 本机核实 | 硬件与系统信息 |
| `procguard` | 程序专杀 | 指定进程的定时清理 |
| `uninstaller` | 应用卸载 | 已安装程序的卸载入口 |
| `startup` | 开机启动项 | 启动项的新增与禁用 |
| `logs` | 运行日志 | 按会话查看日志 |
| `feedback` | 反馈中心 | 问题与建议的提交入口 |
| `feedback-form` | 反馈表单 | 反馈中心内的填写页 |
| `changelog` | 更新日志 | 版本变更记录 |
| `settings` | 设置 | 应用设置总页 |
| `hubai-settings` | Hub AI 设置 | 模型服务的配置 |
| `hubai` | Hub AI | 内置助手的对话页 |

## 工具一览

内置工具既可以用 `csh://tool/<标识>` 打开，也可以用 `csh://page/<标识>`，两种写法等价。表中列出全部工具。

| 标识 | 工具 |
| --- | --- |
| `image-color` | 图片取色 |
| `pick-number` | 随机抽号 |
| `timer` | 课堂计时器 |
| `clock` | 全屏时钟 |
| `encoding` | 编码与哈希工具 |
| `autoshutdown` | 自动关机 |
| `function-plot` | 函数图像 |
| `equation` | 方程求解 |
| `chemistry` | 化学方程式配平 |
| `disk-map` | 磁盘空间 |
| `easiguard` | 白板专杀 |
| `appdata` | AppData 管理 |
| `repair` | 系统修复 |
| `pagefile` | 虚拟内存 |
| `ghostfile` | 幽灵文件清理 |
| `sysfeatures` | 系统功能 |
| `systemtweaks` | 系统优化 |
| `ppttouch` | PowerPoint 触控优化 |

## 设置分组

设置页较长，链接可以直接滚到对应的分组。以下分组名称均可用，同一行内的多个写法效果相同。

| 分组 | 对应的设置内容 |
| --- | --- |
| `appearance`、`theme` | 颜色模式与背景材质 |
| `startup` | 开机自动启动 |
| `hubai`、`ai` | Hub AI 与模型服务 |
| `cache` | 本地缓存 |
| `logs`、`log` | 本地日志 |
| `content` | 软件内容来源 |
| `update`、`updates`、`version` | 检查更新与更新通道 |
| `license`、`auth` | 授权状态 |
| `about` | 版本信息与关于 |

## 文章之间的引用

`csh://article/<文章标识>` 的标识就是文章所在文件夹的名称。例如本文的标识为 `in-app-links`，另一篇文章可以直接写：

```markdown
相关步骤见[教室电脑常用快捷键与设置入口](csh://article/windows-shortcuts)。
```

当前可用的文章标识：

| 标识 | 标题 |
| --- | --- |
| `c-drive-cleanup` | C 盘空间不足时的清理顺序 |
| `classroom-pc-checklist` | 开学前教室电脑检查清单 |
| `windows-shortcuts` | 教室电脑常用快捷键与设置入口 |
| `in-app-links` | 文档里的应用内跳转链接 |

文章尚在校对时，标识可以先行使用；正文页找不到对应文章时会给出提示，不会影响其它段落。

## Windows 设置

以 `ms-settings:` 开头的链接不在应用内跳转，而是交给 Windows 打开系统设置。这类地址由系统定义，写法原样保留：

| 写法 | 打开的系统页面 |
| --- | --- |
| `ms-settings:windowsupdate` | Windows 更新 |
| `ms-settings:storagesense` | 存储感知 |
| `ms-settings:display` | 显示 |
| `ms-settings:appsfeatures` | 已安装的应用 |
| `ms-settings:defaultapps` | 默认应用 |
| `ms-settings:about` | 系统信息 |

完整的可用列表由系统提供，填写时以本机设置页的地址为准。

## 书写约定

- 链接文字写页面或工具的名称，不写「点这里」「查看」这类无法预判去向的词。
- 标识区分大小写以外的书写差异均被接受：`CSH://Page/Downloads`、`csh:page/downloads` 与 `csh://page/downloads` 都指向同一页。
- 标识写错不会中断阅读：页面链接会停在目标页顶部，文章链接由正文页提示找不到，两种情况都不会弹出错误提示。
- 一处链接指向一个位置。需要连续引导时，分句书写，不把多个地址塞进同一段文字。
- 页面与工具会随版本增减。新增页面后本文可能滞后，以应用内的实际入口为准。

## 完整示例

以下片段取自一篇桌面清理类文章，演示页面、工具与文章链接的配合使用：

```markdown
清理前先确认占用分布。打开[磁盘空间](csh://tool/disk-map)，选择系统盘，
等待扫描完成后逐层展开目录。

系统盘剩余空间不足一成的，先按[C 盘空间不足时的清理顺序](csh://article/c-drive-cleanup)
处理下载目录与回收站，再回到本页复查。

清理产生的记录可以在[运行日志](csh://page/logs)中核对，日志按会话分目录保存。
```

以上地址均可在正文中直接使用。投稿时无需额外声明，应用会在渲染正文时识别。

# 文档库

电脑优化相关的教程文章。桌面版 ClassSoftwareHub 直接从本目录读取，按文章缓存在本机。

本目录位于桌面端仓库（`c1201y/ClassSoftwareHub-Desktop`），不随站点仓库发布；改动推送到 `main` 后，客户端下次同步即可看到。

## 目录结构

每篇文章一个文件夹，文件夹名就是文章 id（小写字母、数字、横杠）：

```
文档库/
└─ c-drive-cleanup/
   ├─ meta.json          文章信息（标题、作者、简介、标签、格式）
   ├─ article.md         正文，文件名由 meta.json 的 file 指定
   └─ images/            正文引用的图片，可选
```

以 `_` 开头的文件夹不会被读取，`_模板/` 供投稿时参考。

## meta.json

| 字段 | 说明 |
|---|---|
| `title` | 文章标题 |
| `author` | 作者署名 |
| `contact` | 联系方式，可留空；仅用于溯源，不在正文显示 |
| `summary` | 一句话简介，列表页显示 |
| `tags` | 标签数组 |
| `type` | `markdown` / `txt` / `docx` / `pdf` |
| `file` | 正文字段名，例如 `article.md` |
| `date` | 最后更新日期，`YYYY-MM-DD` |
| `order` | 列表顺序，越小越靠前；省略表示排最后 |

正文支持 md、txt、docx、pdf。md 可写标题、列表（含任务列表）、引用、代码块、表格、图片与链接；docx 读取标题、段落、列表、超链接与图片；pdf 按页显示。

## 正文里的链接

| 写法 | 去向 |
|---|---|
| `csh://article/<id>` | 另一篇文章 |
| `csh://page/<tag>` | 应用内页面，例如 `csh://page/apps` |
| `csh://tool/<id>` | 内置工具，例如 `csh://tool/disk-map` |
| `csh://settings/<分组>` | 设置页的某个分组 |
| `ms-settings:<页面>` | Windows 设置，例如 `ms-settings:storagesense` |
| `https://…` | 浏览器打开 |

## 投稿

通过 [文档投稿](https://github.com/c1201y/ClassSoftwareHub-Desktop/issues/new?template=doc-submission.yml) 填写标题、文章 id、署名、简介与正文。提交后会自动整理成上面的目录结构、跑一遍校验，并开一个草稿 PR；维护者核对内容后合并，客户端下次同步即可看到。

issue 无法附带图片，正文里的图片请使用网络直链；需要本地图片时改为直接提交 PR，把图片放进 `images/`。

投稿和直接提交都要过 [校验文档库](https://github.com/c1201y/ClassSoftwareHub-Desktop/actions/workflows/validate-docs.yml) 的检查：目录名、`meta.json`、正文文件与图片引用按客户端的读取口径核对，错误不修好不会被合入。

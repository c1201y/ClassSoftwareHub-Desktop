// 把站点工程的内容包（dist/content）拷进桌面端的发布目录 dist/app/content。
//
// 为什么需要：桌面端的软件清单来自"内容包"，站点那边 content/manifest.json 一直没发布，
// 结果新机器装完打开就是空清单。这里把一份内容包**随安装包一起发出去**，
// 装机就有清单（离线也看得到），联网后 ContentUpdater 会从云端拉新版覆盖。
//
// 用法：
//   node tools/sync-content.mjs                       # 用默认的站点工程路径
//   node tools/sync-content.mjs "D:\path\to\content"  # 指定内容包目录
//
// 时机：dotnet publish 之后、ISCC 之前（ISCC 会把 dist/app/* 整个打进安装包）。

import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const DEFAULT_SOURCE =
  'C:/Users/Programmer_Nick/OneDrive/\u6587\u6863/Visual Studio 18 \u9879\u76ee\u6587\u4ef6/ClassSoftwareHub/dist/content';

const source = path.resolve(process.argv[2] ?? DEFAULT_SOURCE);
const target = path.resolve('dist/app/content');

function fail(msg) {
  console.error('✗ ' + msg);
  process.exit(1);
}

if (!fs.existsSync(path.join(source, 'apps'))) {
  fail(`内容包目录不对（里面得有 apps/）：${source}\n  先去站点工程跑 scripts/build-content.mjs 生成 dist/content`);
}
if (!fs.existsSync(path.resolve('dist/app'))) {
  fail('还没 dotnet publish（dist/app 不存在）→ 先发布再拷内容');
}

fs.rmSync(target, { recursive: true, force: true });
fs.mkdirSync(target, { recursive: true });

let files = 0;
let bytes = 0;

function walk(dir, rel = '') {
  for (const entry of fs.readdirSync(path.join(dir, rel), { withFileTypes: true })) {
    const r = rel ? `${rel}/${entry.name}` : entry.name;
    const from = path.join(dir, r);
    const to = path.join(target, r);
    if (entry.isDirectory()) {
      fs.mkdirSync(to, { recursive: true });
      walk(dir, r);
    } else {
      fs.copyFileSync(from, to);
      files++;
      bytes += fs.statSync(to).size;
    }
  }
}

walk(source);

const manifest = path.join(target, 'manifest.json');
if (!fs.existsSync(manifest)) fail('拷完了但没 manifest.json —— 内容包不完整，检查一下站点那边的构建');

console.log(`✓ 内容包已拷进 dist/app/content：${files} 个文件，${(bytes / 1024).toFixed(0)} KB`);
console.log('  下一步：ISCC installer/ClassSoftwareHub.iss（会把 dist/app 整个打进去）');

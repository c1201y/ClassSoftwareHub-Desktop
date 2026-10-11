#!/usr/bin/env node
// ══════════════════════════════════════════════════════════════════════
//  条款同步：站点《法律文本.ts》 → 桌面端
//
//  【唯一真源】站点仓库根目录的 `法律文本.ts`（用户协议 / 隐私政策 / 免责声明，
//  中英双语，站点「条款」页 #/agreement 和站点设置页「关于」区都读它）。
//  桌面端不另存一份手抄件 —— 本脚本把它转成桌面端要用的两个文件：
//
//    ① Core/LegalText.cs    —— 设置页「条款与声明」阅读窗口的原生数据（只取中文）
//    ② installer/Legal.rtf  —— 安装程序那一页（Inno Setup 的 LicenseFile）
//
//  【用法】（在本仓库根目录）
//      node tools/sync-legal.mjs
//      node tools/sync-legal.mjs --site "C:\\...\\ClassSoftwareHub"   指定站点仓库位置
//      node tools/sync-legal.mjs --src  "C:\\...\\法律文本.ts"        直接给一份源文件
//
//  【取源规则】按顺序：① --src 指定的文件；② 站点仓库工作区里的 `法律文本.ts`；
//  ③ 退而用 `git show origin/main:法律文本.ts`（要求先 `git fetch` 过）。
//  ⇒ 无论哪种，都**不会改动站点仓库**。
//  ⚠️ ③ 依赖子进程调用 git；某些受限环境（比如被沙箱托管的会话）里 node 起不了子进程，
//     会报 `spawnSync git EBUSY` —— 那就先用 git 把文件导出来，再走 ①。
//
//  ⛔ 别手改生成物（改条款请改站点那份 `法律文本.ts`，再跑一次本脚本）。
//  ⚠️ 站点是双语的，桌面端界面全中文 ⇒ 只取 zh，不取 en。
// ══════════════════════════════════════════════════════════════════════

import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));   // <repo>/tools
const repo = path.resolve(here, '..');                        // <repo>

// ── 站点仓库位置：默认与本仓库同级（同在一个解决方案目录下）──────────────
const siteArg = process.argv.indexOf('--site');
const siteRepo = siteArg > 0 && process.argv[siteArg + 1]
  ? path.resolve(process.argv[siteArg + 1])
  : path.resolve(repo, '..', 'ClassSoftwareHub');

const SRC_NAME = '法律文本.ts';
const REL_SRC = path.join(siteRepo, SRC_NAME);

const srcArg = process.argv.indexOf('--src');

// ── 1. 取源（--src → 工作区文件 → git show origin/main）──────────────────
function readSource() {
  if (srcArg > 0 && process.argv[srcArg + 1]) {
    const p = path.resolve(process.argv[srcArg + 1]);
    if (!fs.existsSync(p)) throw new Error(`--src 指定的文件不存在：${p}`);
    return { text: fs.readFileSync(p, 'utf8'), from: p };
  }
  if (fs.existsSync(REL_SRC)) {
    return { text: fs.readFileSync(REL_SRC, 'utf8'), from: '工作区' };
  }
  if (!fs.existsSync(siteRepo)) {
    throw new Error(`找不到站点仓库：${siteRepo}\n  用 --site "<路径>" 指定，或先把它放在本仓库同级目录。`);
  }
  try {
    const text = execFileSync(
      'git',
      ['-c', 'core.quotepath=false', '-C', siteRepo, 'show', `origin/main:${SRC_NAME}`],
      { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 });
    return { text, from: 'git origin/main' };
  } catch (e) {
    throw new Error(`从 ${siteRepo} 取不到 ${SRC_NAME}：\n  工作区没有该文件，\`git show origin/main\` 也失败。\n  先在该仓库 \`git fetch\`（或 pull）一下再跑本脚本。\n${e.message}`);
  }
}

// ── 2. 解析 TS（Node 自带类型擦除，直接 import 拿对象；⛔ 不手写正则解析）──
async function loadDocs(tsSource) {
  const tmp = path.join(os.tmpdir(), `csh-legal-${process.pid}-${Date.now()}.mts`);
  fs.writeFileSync(tmp, tsSource, 'utf8');
  try {
    const mod = await import(pathToFileURL(tmp).href);
    const docs = mod.default;
    if (!Array.isArray(docs) || docs.length === 0) {
      throw new Error('默认导出不是法律文档数组（站点那份文件的导出结构变了？）');
    }
    return docs;
  } finally {
    try { fs.rmSync(tmp, { force: true }); } catch { /* 临时文件，删不掉也不影响 */ }
  }
}

// ── 3. 生成 Core/LegalText.cs ──────────────────────────────────────────
/** C# verbatim 字符串：只需把双引号翻倍，反斜杠天然安全。 */
const csStr = (s) => '@"' + String(s ?? '').replace(/"/g, '""') + '"';

function genCsharp(docs) {
  const L = [];
  L.push('// ══════════════════════════════════════════════════════════════════════');
  L.push('//  ⚠️ 本文件由 tools/sync-legal.mjs 自动生成，⛔ 别手改（下次同步会被覆盖）。');
  L.push('//');
  L.push('//  条款的真源是**站点仓库根目录**的 `法律文本.ts`（中英双语，站点「条款」页');
  L.push('//  #/agreement 用的就是它）。桌面端不另存手抄件 —— 要改条款：');
  L.push('//    ① 改站点那份 `法律文本.ts`；');
  L.push('//    ② 在本仓库跑 `node tools/sync-legal.mjs`（会重写本文件与 installer/Legal.rtf）。');
  L.push('//');
  L.push('//  ⚠️ 站点是双语的，桌面端界面全中文 ⇒ 这里只取 zh，没有 en。');
  L.push('// ══════════════════════════════════════════════════════════════════════');
  L.push('');
  L.push('namespace ClassSoftwareHub.Desktop.Core;');
  L.push('');
  L.push('/// <summary>一份法律文档里的一节：小节标题 + 该节下的条款（每条一行）。</summary>');
  L.push('public sealed class LegalSection');
  L.push('{');
  L.push('    public string Heading { get; init; } = "";');
  L.push('    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();');
  L.push('}');
  L.push('');
  L.push('/// <summary>一份法律文档（用户协议 / 隐私政策 / 免责声明之一）。</summary>');
  L.push('public sealed class LegalDoc');
  L.push('{');
  L.push('    /// <summary>站点里的稳定标识：agreement / privacy / disclaimer。</summary>');
  L.push('    public string Key { get; init; } = "";');
  L.push('');
  L.push('    /// <summary>版本号（如 v1.0）与生效日期（如 2026-10-05），站点改条款时会同步更新。</summary>');
  L.push('    public string Version { get; init; } = "";');
  L.push('    public string Effective { get; init; } = "";');
  L.push('');
  L.push('    /// <summary>文档标题（也是设置页入口卡片与阅读窗口标签上显示的文字）。</summary>');
  L.push('    public string Title { get; init; } = "";');
  L.push('');
  L.push('    /// <summary>标题下的引言段（站点允许留空 ⇒ 这里可能是空串）。</summary>');
  L.push('    public string Intro { get; init; } = "";');
  L.push('');
  L.push('    public IReadOnlyList<LegalSection> Sections { get; init; } = Array.Empty<LegalSection>();');
  L.push('');
  L.push('    /// <summary>「版本 v1.0 · 生效日期 2026-10-05」这种一行摘要（站点「条款」页副标题同款）。</summary>');
  L.push('    public string VersionLine =>');
  L.push('        string.IsNullOrEmpty(Effective) ? Version : $"{Version} · 生效日期 {Effective}";');
  L.push('}');
  L.push('');
  L.push('/// <summary>');
  L.push('/// 桌面端内置的三份条款（由站点《法律文本.ts》同步而来，见本文件头的说明）。');
  L.push('/// 用途：① 设置页「条款与声明」的三个入口 + 阅读窗口；② 安装程序那一页（installer/Legal.rtf）。');
  L.push('/// </summary>');
  L.push('public static class LegalText');
  L.push('{');
  L.push('    /// <summary>三份文档，顺序与站点「条款」页的标签一致（用户协议 → 隐私政策 → 免责声明）。</summary>');
  L.push('    public static IReadOnlyList<LegalDoc> All { get; } = new LegalDoc[]');
  L.push('    {');

  for (const d of docs) {
    const zh = d.zh ?? {};
    L.push('        new()');
    L.push('        {');
    L.push(`            Key = ${csStr(d.key)},`);
    L.push(`            Version = ${csStr(d.version)},`);
    L.push(`            Effective = ${csStr(d.effective)},`);
    L.push(`            Title = ${csStr(zh.title)},`);
    L.push(`            Intro = ${csStr(zh.intro)},`);
    L.push('            Sections = new LegalSection[]');
    L.push('            {');
    for (const s of zh.sections ?? []) {
      const items = (s.items ?? []).map((t) => csStr(t)).join(', ');
      L.push(`                new() { Heading = ${csStr(s.heading)}, Items = new[] { ${items} } },`);
    }
    L.push('            }');
    L.push('        },');
  }

  L.push('    };');
  L.push('');
  L.push('    /// <summary>按 key（agreement / privacy / disclaimer）取一份文档；找不到返回 null。</summary>');
  L.push('    public static LegalDoc? Find(string key) =>');
  L.push('        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));');
  L.push('}');
  L.push('');
  return L.join('\n');
}

// ── 4. 生成 installer/Legal.rtf（Inno Setup 的 LicenseFile）────────────
//  ⚠️ 用 RTF 而不是 txt：Inno 的**纯文本**许可文件「不会自动换行」，得自己按列硬换行，
//     中文按字符算宽度更是一团糟；RTF 交给 RichEdit 排版，自带换行与字体。
//  ⚠️ 中文全部走 \uNNNN? 转义（\uc1 = 一个 fallback 字符），生成物是纯 ASCII ⇒ 彻底免编码烦恼。
function rtfEscape(s) {
  let out = '';
  for (const ch of String(s ?? '')) {
    const code = ch.codePointAt(0);
    if (ch === '\\') out += '\\\\';
    else if (ch === '{') out += '\\{';
    else if (ch === '}') out += '\\}';
    else if (code < 0x20) out += ' ';
    else if (code < 0x80) out += ch;
    else if (code <= 0xFFFF) out += `\\u${code > 32767 ? code - 65536 : code}?`;
    else {
      // BMP 之外（emoji 之类）：拆成代理对，逐半转义
      const hi = Math.floor((code - 0x10000) / 0x400) + 0xD800;
      const lo = ((code - 0x10000) % 0x400) + 0xDC00;
      out += `\\u${hi - 65536}?\\u${lo - 65536}?`;
    }
  }
  return out;
}

/** 正文一行（9pt）。 */
const rtfLine = (s) => `\\pard\\f0\\fs18 ${rtfEscape(s)}\\par\n`;
/** 小节标题 / 文档标题（加粗，大一号）。 */
const rtfTitle = (s, size = 22) => `\\pard\\f0\\fs${size}\\b ${rtfEscape(s)}\\b0\\par\n`;

function genRtf(docs) {
  let out = '';
  out += '{\\rtf1\\ansi\\ansicpg1252\\deff0\n';
  out += '{\\fonttbl{\\f0\\fnil\\fcharset134 Microsoft YaHei;}}\n';
  out += '\\viewkind4\\uc1\n';

  docs.forEach((d, i) => {
    const zh = d.zh ?? {};
    if (i > 0) {
      // 文档之间空一行 + 一条由短横线拼出的分隔（RTF 里没法画线，就用字符）
      out += '\\pard\\f0\\fs18\\par\n';
      out += rtfLine('————————————————————————————————————');
    }
    out += rtfTitle(`${zh.title}（${d.version}　${d.effective} 起生效）`, 24);
    out += '\\pard\\f0\\fs18\\par\n';
    if (zh.intro) {
      out += rtfLine(zh.intro);
      out += '\\pard\\f0\\fs18\\par\n';
    }
    for (const s of zh.sections ?? []) {
      out += rtfTitle(s.heading, 20);
      for (const t of s.items ?? []) out += rtfLine(t);
      out += '\\pard\\f0\\fs18\\par\n';
    }
  });

  out += '}\n';
  return out;
}

// ── 5. 落盘 ────────────────────────────────────────────────────────────
const { text: tsSource, from } = readSource();
const docs = await loadDocs(tsSource);

const csPath = path.join(repo, 'Core', 'LegalText.cs');
const rtfPath = path.join(repo, 'installer', 'Legal.rtf');

fs.writeFileSync(csPath, genCsharp(docs), 'utf8');
fs.writeFileSync(rtfPath, genRtf(docs), 'ascii');

const rel = (p) => path.relative(repo, p).replace(/\\/g, '/');
console.log(`源：${REL_SRC}（${from}）`);
for (const d of docs) {
  const n = (d.zh?.sections ?? []).reduce((a, s) => a + (s.items?.length ?? 0), 0);
  console.log(`  · ${d.key.padEnd(11)} ${d.zh?.title}  ${d.version}  ${d.effective}  ${d.zh?.sections?.length ?? 0} 节 / ${n} 条`);
}
console.log(`已生成：${rel(csPath)}`);
console.log(`已生成：${rel(rtfPath)}`);

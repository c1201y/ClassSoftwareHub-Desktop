#!/usr/bin/env node
// 文档库入库校验：GitHub Actions 与本地共用。
// 规则对齐桌面端 Services/Docs/DocLibrary.cs 与 Controls/DocView.cs 的读取口径，
// 目的是避免「仓库里看着没问题、客户端读不出来」。
//
// 用法：node scripts/validate-docs.mjs [文档库目录]

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve, posix } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(fileURLToPath(new URL('..', import.meta.url)));
const docsDirName = process.argv[2] ?? '文档库';
const docsRoot = resolve(repoRoot, docsDirName);

const ID_PATTERN = /^[a-z0-9][a-z0-9-]*$/;
const META_KEYS = new Set([
  'title', 'author', 'contact', 'summary', 'tags', 'type', 'file', 'date', 'order',
]);
const TYPE_EXTENSIONS = {
  markdown: ['.md', '.markdown'],
  txt: ['.txt'],
  docx: ['.docx'],
  pdf: ['.pdf'],
};
const SUPPORTED_EXTENSIONS = new Set(Object.values(TYPE_EXTENSIONS).flat());

const MAX_MAIN_BYTES = 50 * 1024 * 1024;
const MAX_ASSET_BYTES = 20 * 1024 * 1024;
const MAX_TOTAL_BYTES = 200 * 1024 * 1024;
const MAX_MARKDOWN_SCAN_BYTES = 5 * 1024 * 1024;

const errors = [];
const warnings = [];

const error = (file, message) => errors.push({ file, message });
const warn = (file, message) => warnings.push({ file, message });

const articleDirs = [];
const ids = new Set();

function relativeToDocs(path) {
  return `${docsDirName}/${path}`;
}

function walkFiles(dir, prefix = '') {
  const out = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const rel = prefix ? `${prefix}/${entry.name}` : entry.name;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      out.push(...walkFiles(full, rel));
    } else if (entry.isFile()) {
      out.push({ rel, full, size: statSync(full).size });
    } else {
      warn(relativeToDocs(rel), '不是普通文件（符号链接等），同步时会跳过');
    }
  }
  return out;
}

function parseDate(text) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  const valid = date.getUTCFullYear() === year
    && date.getUTCMonth() === month - 1
    && date.getUTCDate() === day;
  return valid ? date : null;
}

function normalizeRelative(value) {
  let text = String(value).trim().replace(/\\/g, '/');
  if (text.startsWith('./')) text = text.slice(2);
  text = text.replace(/^\/+/, '');
  return posix.normalize(text).replace(/^\.\//, '');
}

function extOf(path) {
  const dot = path.lastIndexOf('.');
  return dot < 0 ? '' : path.slice(dot).toLowerCase();
}

function checkMeta(id, dir, files) {
  const metaFile = files.find((file) => file.rel === 'meta.json');
  if (!metaFile) {
    const nearMiss = files.find((file) => file.rel.toLowerCase() === 'meta.json');
    if (nearMiss) {
      error(relativeToDocs(`${id}/${nearMiss.rel}`), `文件名必须全小写：${nearMiss.rel}`);
    } else {
      error(relativeToDocs(id), '缺少 meta.json');
    }
    return null;
  }

  const metaRel = relativeToDocs(`${id}/meta.json`);
  let text;
  try {
    text = readFileSync(metaFile.full, 'utf8');
  } catch (ex) {
    error(metaRel, `读取失败：${ex.message}`);
    return null;
  }

  if (text.includes('\uFFFD')) {
    error(metaRel, '不是有效的 UTF-8 文本');
    return null;
  }

  let meta;
  try {
    meta = JSON.parse(text.replace(/^\uFEFF/, ''));
  } catch (ex) {
    error(metaRel, `JSON 解析失败：${ex.message}`);
    return null;
  }

  if (meta === null || typeof meta !== 'object' || Array.isArray(meta)) {
    error(metaRel, '顶层必须是对象');
    return null;
  }

  for (const key of Object.keys(meta)) {
    if (!META_KEYS.has(key)) warn(metaRel, `未知字段：${key}（客户端会忽略）`);
  }

  const str = (key) => (typeof meta[key] === 'string' ? meta[key].trim() : '');

  const title = str('title');
  if (!title) error(metaRel, 'title 不能为空');
  else if (title.length > 60) warn(metaRel, `title 较长（${title.length} 字），列表卡片可能折行`);

  const author = str('author');
  if (!author) error(metaRel, 'author 不能为空');
  else if (author.length > 40) warn(metaRel, `author 较长（${author.length} 字）`);

  if (meta.contact !== undefined && typeof meta.contact !== 'string') {
    error(metaRel, 'contact 必须是字符串');
  }

  const summary = str('summary');
  if (!summary) error(metaRel, 'summary 不能为空');
  else if (summary.length > 60) warn(metaRel, `summary 较长（${summary.length} 字），列表页会截断`);

  const dateText = str('date');
  if (!dateText) {
    error(metaRel, '缺少 date（YYYY-MM-DD）');
  } else if (!parseDate(dateText)) {
    error(metaRel, `date 不是有效日期：${dateText}`);
  }

  if (meta.order !== undefined) {
    if (!Number.isInteger(meta.order)) error(metaRel, 'order 必须是整数');
  }

  if (meta.tags !== undefined) {
    if (!Array.isArray(meta.tags)) {
      error(metaRel, 'tags 必须是字符串数组');
    } else {
      for (const tag of meta.tags) {
        if (typeof tag !== 'string' || !tag.trim()) error(metaRel, 'tags 里不能有空项或非字符串');
      }
      if (meta.tags.length > 8) warn(metaRel, `tags 有 ${meta.tags.length} 个，列表页显示会很长`);
    }
  }

  const type = str('type').toLowerCase();
  if (!type) {
    error(metaRel, '缺少 type');
  } else if (!TYPE_EXTENSIONS[type]) {
    error(metaRel, `type 不受支持：${type}（可用 markdown / txt / docx / pdf）`);
  }

  const fileRaw = str('file');
  if (!fileRaw) {
    error(metaRel, '缺少 file');
    return null;
  }
  const file = normalizeRelative(fileRaw);

  const content = files.find((entry) => entry.rel === file)
    ?? files.find((entry) => entry.rel.toLowerCase() === file.toLowerCase());

  if (!content) {
    error(metaRel, `file 指向的文件不存在：${file}`);
    return null;
  }
  if (content.rel !== file) {
    error(metaRel, `file 与实际文件名大小写不一致：meta 写的是 ${file}，仓库里是 ${content.rel}`);
  }

  const extension = extOf(content.rel);
  if (!SUPPORTED_EXTENSIONS.has(extension)) {
    error(metaRel, `正文格式不受支持：${extension}（可用 .md / .markdown / .txt / .docx / .pdf）`);
  } else if (TYPE_EXTENSIONS[type] && !TYPE_EXTENSIONS[type].includes(extension)) {
    error(metaRel, `type 与正文扩展名不一致：type=${type}，文件是 ${extension}`);
  }

  if (content.size > MAX_MAIN_BYTES) {
    error(relativeToDocs(`${id}/${content.rel}`), `正文超过 50 MB（${Math.round(content.size / 1048576)} MB）`);
  } else if (content.size > 10 * 1024 * 1024) {
    warn(relativeToDocs(`${id}/${content.rel}`), `正文较大（${Math.round(content.size / 1048576)} MB），首次打开会慢`);
  }

  for (const entry of files) {
    if (entry.rel === 'meta.json') continue;
    if (entry.size > MAX_ASSET_BYTES) {
      error(relativeToDocs(`${id}/${entry.rel}`), `单个文件超过 20 MB（${Math.round(entry.size / 1048576)} MB）`);
    }
    if (entry.rel.split('/').some((part) => part.startsWith('.'))) {
      warn(relativeToDocs(`${id}/${entry.rel}`), '以 . 开头的文件/目录不会进入客户端缓存');
    }
  }

  const total = files.reduce((sum, entry) => sum + entry.size, 0);
  if (total > MAX_TOTAL_BYTES) {
    error(relativeToDocs(id), `整篇文章超过 200 MB（${Math.round(total / 1048576)} MB）`);
  } else if (total > 100 * 1024 * 1024) {
    warn(relativeToDocs(id), `整篇文章较大（${Math.round(total / 1048576)} MB）`);
  }

  return { id, meta, type, file, content, metaRel, dir };
}

function checkMarkdownBody(info, files) {
  if (info.type !== 'markdown' || info.content.size > MAX_MARKDOWN_SCAN_BYTES) return;

  let text;
  try {
    text = readFileSync(info.content.full, 'utf8');
  } catch {
    return;
  }

  if (text.includes('\uFFFD')) {
    error(relativeToDocs(`${info.id}/${info.content.rel}`), '不是有效的 UTF-8 文本');
    return;
  }

  const fileSet = new Set(files.map((entry) => entry.rel));

  const imagePattern = /!\[[^\]]*\]\(\s*(<[^>]*>|[^)\s]+)(?:\s+("[^"]*"|'[^']*'))?\s*\)/g;
  for (const match of text.matchAll(imagePattern)) {
    const raw = match[1].replace(/^<|>$/g, '').trim();
    if (/^(https?:|data:)/i.test(raw)) {
      warn(relativeToDocs(`${info.id}/${info.content.rel}`),
        `外链图片需要联网才能显示：${raw.slice(0, 80)}`);
      continue;
    }
    if (raw.startsWith('/')) {
      error(relativeToDocs(`${info.id}/${info.content.rel}`),
        `图片不能用绝对路径：${raw}`);
      continue;
    }
    const normalized = normalizeRelative(raw);
    if (normalized.split('/').includes('..')) {
      error(relativeToDocs(`${info.id}/${info.content.rel}`),
        `图片路径不能跳出文章目录：${raw}`);
    } else if (!fileSet.has(normalized)) {
      error(relativeToDocs(`${info.id}/${info.content.rel}`),
        `图片不存在：${raw}`);
    }
  }

  const articleLinkPattern = /csh:\/\/article\/([^)\s"'<>]+)/g;
  for (const match of text.matchAll(articleLinkPattern)) {
    let target = match[1];
    try { target = decodeURIComponent(target); } catch { /* 保持原样 */ }
    if (!ids.has(target.toLowerCase())) {
      warn(relativeToDocs(`${info.id}/${info.content.rel}`),
        `链接指向的文章不存在：csh://article/${target}`);
    }
  }
}

function main() {
  let entries;
  try {
    entries = readdirSync(docsRoot, { withFileTypes: true });
  } catch (ex) {
    error(docsDirName, `目录读取失败：${ex.message}`);
    return;
  }

  for (const entry of entries) {
    if (!entry.isDirectory()) continue;
    const id = entry.name;
    if (id.startsWith('_') || id.startsWith('.')) continue;
    articleDirs.push({ id, dir: join(docsRoot, id) });
    ids.add(id.toLowerCase());
  }

  const loaded = [];
  for (const { id, dir } of articleDirs) {
    const files = walkFiles(dir);
    if (!ID_PATTERN.test(id)) {
      error(relativeToDocs(id), '目录名只能用小写字母、数字与横杠，且不能以横杠结尾');
    }
    if (id.length > 64) error(relativeToDocs(id), `目录名过长（${id.length} 字）`);
    if (id.endsWith('-')) error(relativeToDocs(id), '目录名不能以横杠结尾');

    const info = checkMeta(id, dir, files);
    if (info) {
      loaded.push({ ...info, files });
    }
  }

  const seenTitles = new Map();
  const seenOrders = new Map();
  for (const info of loaded) {
    const title = String(info.meta.title ?? '').trim();
    if (seenTitles.has(title)) {
      warn(info.metaRel, `标题与 ${seenTitles.get(title)} 重复：${title}`);
    } else {
      seenTitles.set(title, info.metaRel);
    }

    if (Number.isInteger(info.meta.order)) {
      const key = info.meta.order;
      if (seenOrders.has(key)) {
        warn(info.metaRel, `order 与 ${seenOrders.get(key)} 重复：${key}`);
      } else {
        seenOrders.set(key, info.metaRel);
      }
    }

    checkMarkdownBody(info, info.files);
  }

  for (const item of warnings) report('warning', item);
  for (const item of errors) report('error', item);

  const summary = `文档库校验：${articleDirs.length} 篇文章，${errors.length} 个错误，${warnings.length} 个提醒`;
  console.log(summary);

  if (errors.length > 0) {
    console.log('校验未通过。按上面的错误修正后重新提交。');
    process.exit(1);
  }

  console.log('校验通过。');
}

function report(level, item) {
  if (process.env.GITHUB_ACTIONS) {
    const message = item.message.replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
    console.log(`::${level} file=${item.file},line=1::${message}`);
  } else {
    console.log(`  [${level === 'error' ? '错误' : '提醒'}] ${item.file}：${item.message}`);
  }
}

main();

// publish-release.mjs — 发一个「更新通道」能认的 GitHub Release
//
// 用法（在工程根目录）：
//   set GITHUB_TOKEN=ghp_xxx            # 只在当前终端，别写进任何文件
//   node tools/publish-release.mjs --tag dv1.0.0-insider1.3 --channel insider ^
//        --installer "dist\installer\ClassSoftwareHub-Setup-dv1.0.0-insider1.3.exe" ^
//        --notes notes.md
//
// 参数：
//   --tag        必填。正式版 dv1.0.0 / 预发布 dv1.0.0-insider1.3（跟 ShellConfig.ShellVersion 对齐）
//   --channel    stable | insider（insider 会自动勾 Pre-release）
//   --installer  必填。安装包路径；同名 .md5 会自动生成并一起上传
//   --notes      可选。更新说明文件（md/txt，会原样显示在更新对话框里）
//   --name       可选。Release 标题，默认就用 tag
//   --draft     可选。发成草稿（不发布，先看一眼）
//   --dry-run   可选。只打印要干什么，不碰 GitHub
//
// 约定（跟 Core/ShellConfig.cs 里的说明一致，改了要一起改）：
//   tag   ：dv<版本>（正式）/ dv<版本>-insider<x>（勾 Pre-release）
//   资产  ：安装包（名字里带 setup 会被识别）+ 同名 .md5（MD5 校验用）
//   正文  ：Release body = 更新内容

import { createHash } from 'node:crypto';
import { readFile, writeFile, stat } from 'node:fs/promises';
import { basename, resolve } from 'node:path';

const OWNER = 'c1201y';
const REPO = 'ClassSoftwareHub-Desktop';

// ── 参数 ────────────────────────────────────────────────────────────
const args = process.argv.slice(2);
function opt(name, fallback = '') {
  const i = args.indexOf('--' + name);
  if (i < 0) return fallback;
  const v = args[i + 1];
  return !v || v.startsWith('--') ? 'true' : v;
}
const tag = opt('tag');
const channel = (opt('channel', 'insider') || 'insider').toLowerCase();
const installer = opt('installer');
const notesFile = opt('notes');
const releaseName = opt('name') || tag;
const isDraft = args.includes('--draft');
const dryRun = args.includes('--dry-run');
const token = process.env.GITHUB_TOKEN || process.env.GH_TOKEN || '';

if (!tag || !installer) {
  console.error('缺参数：--tag 和 --installer 是必填。见文件头的用法。');
  process.exit(2);
}
if (channel !== 'stable' && channel !== 'insider') {
  console.error(`--channel 只能是 stable / insider（现在给的是 "${channel}"）`);
  process.exit(2);
}

// tag 跟版本号形状对不上时提醒一下（别让正式版被打成预发布）
const looksPrerelease = tag.includes('insider');
if (looksPrerelease && channel === 'stable') {
  console.error(`tag "${tag}" 看着是预发布，但 --channel stable。要么改 tag，要么 --channel insider。`);
  process.exit(2);
}
if (!looksPrerelease && channel === 'insider') {
  console.error(`tag "${tag}" 没有 insider 字样，却要走预览通道。确认一下就改成 --channel stable。`);
  process.exit(2);
}

if (!token && !dryRun) {
  console.error('没有 GITHUB_TOKEN（或 GH_TOKEN）环境变量，发不了 Release。');
  process.exit(2);
}

const api = 'https://api.github.com';
const headers = {
  Accept: 'application/vnd.github+json',
  'X-GitHub-Api-Version': '2022-11-28',
  'User-Agent': 'ClassSoftwareHub-Release',
  ...(token ? { Authorization: 'Bearer ' + token } : {}),
};

// ── 算哈希 + 写 .md5 ─────────────────────────────────────────────────
const exePath = resolve(installer);
const buf = await readFile(exePath);
const md5 = createHash('md5').update(buf).digest('hex');
const sha = createHash('sha256').update(buf).digest('hex');
const sizeMB = (buf.length / 1024 / 1024).toFixed(1);
const md5Path = exePath + '.md5';
// "<md5>  <文件名>"：既能被更新器按文件名匹配，也能整段正则抓到
await writeFile(md5Path, `${md5}  ${basename(exePath)}\n`, 'utf8');

const body = notesFile ? await readFile(resolve(notesFile), 'utf8') : '';

console.log('── 要发的 Release ──────────────────────────');
console.log(`仓库    : ${OWNER}/${REPO}`);
console.log(`tag     : ${tag}${isDraft ? ' (草稿)' : ''}`);
console.log(`通道    : ${channel}${channel === 'insider' ? ' → 勾 Pre-release' : ' → 正式版'}`);
console.log(`标题    : ${releaseName}`);
console.log(`安装包  : ${basename(exePath)}  ${sizeMB} MB`);
console.log(`MD5     : ${md5}`);
console.log(`SHA256  : ${sha}`);
console.log(`说明    : ${body ? notesFile + '（' + body.length + ' 字）' : '（空）'}`);
console.log(`多传一个: ${basename(md5Path)}`);

if (dryRun) {
  console.log('\n--dry-run：到此为止，没动 GitHub。');
  process.exit(0);
}

// ── 建 Release ──────────────────────────────────────────────────────
async function gh(url, init = {}) {
  const r = await fetch(url, { ...init, headers: { ...headers, ...(init.headers || {}) } });
  const text = await r.text();
  if (!r.ok) throw new Error(`${init.method || 'GET'} ${url} → ${r.status}\n${text.slice(0, 400)}`);
  return text ? JSON.parse(text) : null;
}

console.log('\n1/2 创建 Release…');
const existing = await gh(`${api}/repos/${OWNER}/${REPO}/releases/tags/${encodeURIComponent(tag)}`).catch(() => null);
if (existing) {
  console.error(`  已经存在 tag ${tag} 的 Release 了：${existing.html_url}\n  要么换 tag（版本号加一），要么去网页上删了重来。`);
  process.exit(3);
}

const release = await gh(`${api}/repos/${OWNER}/${REPO}/releases`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    tag_name: tag,
    name: releaseName,
    body,
    draft: isDraft,
    prerelease: channel === 'insider',
  }),
});
console.log('   ✓ ' + release.html_url);

console.log('2/2 上传资产…');
for (const file of [exePath, md5Path]) {
  const data = await readFile(file);
  const name = basename(file);
  const uploadUrl = `https://uploads.github.com/repos/${OWNER}/${REPO}/releases/${release.id}/assets?name=${encodeURIComponent(name)}`;
  const asset = await gh(uploadUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/octet-stream', 'Content-Length': String(data.length) },
    body: data,
    duplex: 'half',
  });
  console.log(`   ✓ ${name}（${(asset.size / 1024 / 1024).toFixed(1)} MB）`);
}

console.log(`\n完成 🎉  ${release.html_url}`);
console.log('客户端：对应通道的用户启动时就会收到这个版本（预发布只推给预览通道）。');

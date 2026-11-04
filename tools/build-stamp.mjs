// build-stamp.mjs — 给官方构建号签名，产出可传给 `-p:CshBuildStampB64=` 的串
//
// 为什么要它：后端《防滥用接入指南》§2.3 规定官方包必须带构建签名，
//   签名 = hex(HMAC-SHA256(CSH_BUILD_SECRET, "v1|" + buildId))
// 密钥不在仓库里（仓库公开），只从环境变量读。
//
// 用法（工程根目录）：
//   export CSH_BUILD_SECRET='...'                       # 只在当前终端，别写进任何文件
//   node tools/build-stamp.mjs --version 1.2.0-insider1.9
//   → 输出一行 base64，直接喂给 dotnet publish：
//      dotnet publish ... -p:CshBuildStampB64="$(node tools/build-stamp.mjs --version 1.2.0-insider1.9)"
//
// 参数：
//   --version <ver>   版本号（不带 dv 前缀）。默认取 Core/ShellConfig.cs 的 ShellVersion
//   --build <id>      直接指定构建号，跳过自动拼（用它就不用 --version）
//   --date <iso>      签发时间，默认取当前 UTC
//   --check <base64>  校验一个已生成的 stamp：打印内容并重新算一次签名比对
//   --quiet           只输出 base64（默认就是这样；加不加都一样，留着给脚本里显式写明）
//
// 构建号格式：dv<版本>+<yyyyMMddTHHmmZ>，例 dv1.2.0-insider1.9+20261104T0144Z
//   —— 服务端要求 ≤96 字符、不含竖线，且必须与签名里的一致。
//
// ⛔ 不要把 CSH_BUILD_SECRET 写进任何文件、命令行历史或日志。本脚本只读环境变量。

import { createHmac } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const run = promisify(execFile);

/// 当前提交的短 SHA（7 位）。取不到（不在 git 仓库里）就退回 UTC 时间戳，保证总能出串。
async function shortSha() {
  try {
    const { stdout } = await run('git', ['rev-parse', '--short=7', 'HEAD']);
    const sha = stdout.trim();
    if (/^[0-9a-f]{7,}$/i.test(sha)) return sha;
  } catch {
    // 落在这里是正常的：导出的源码包没有 .git
  }
  return new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d{3}Z$/, 'Z');
}

const args = process.argv.slice(2);
function opt(name, fallback = '') {
  const i = args.indexOf('--' + name);
  if (i < 0) return fallback;
  const v = args[i + 1];
  return !v || v.startsWith('--') ? 'true' : v;
}

const ALGO = 'v1';

function sign(secret, buildId) {
  return createHmac('sha256', secret).update(`${ALGO}|${buildId}`, 'utf8').digest('hex');
}

function encode(buildId, sig, issuedAt) {
  const payload = `${ALGO}|${buildId}|${sig}|${issuedAt}`;
  return Buffer.from(payload, 'utf8').toString('base64');
}

function decode(b64) {
  const text = Buffer.from(b64.trim(), 'base64').toString('utf8');
  const parts = text.split('|');
  if (parts.length < 4 || parts[0] !== ALGO) return null;
  return { buildId: parts[1], sig: parts[2], issuedAt: parts.slice(3).join('|') };
}

// ── --check：只看内容、再核一次签名 ───────────────────────────────────
const check = opt('check');
if (check) {
  const got = decode(check);
  if (!got) {
    console.error('格式不对：解出来不是 "v1|buildId|sig|issuedAt"。');
    process.exit(2);
  }

  console.log(`构建号  : ${got.buildId}`);
  console.log(`签名    : ${got.sig}`);
  console.log(`签发时间: ${got.issuedAt}`);

  const secret = process.env.CSH_BUILD_SECRET || '';
  if (!secret) {
    console.error('\n没有 CSH_BUILD_SECRET，跳过重算（只做了格式校验）。');
    process.exit(0);
  }

  const expect = sign(secret, got.buildId);
  if (expect === got.sig) {
    console.log('\n✓ 签名与当前密钥一致。');
    process.exit(0);
  }

  console.error(`\n✗ 签名对不上。按当前密钥应为 ${expect}`);
  process.exit(1);
}

// ── 生成 ────────────────────────────────────────────────────────────
const secret = process.env.CSH_BUILD_SECRET || '';
if (!secret) {
  console.error(
    '没有 CSH_BUILD_SECRET 环境变量，签不了。\n' +
    '  官方包必须签名，否则服务端按「自行编译版」处理、要求用户输入激活码。\n' +
    '  只想本地编译（社区版）就别传 -p:CshBuildStampB64，不需要跑本脚本。'
  );
  process.exit(2);
}

let version = opt('version');
if (!version) {
  // 没给就顺手读一下源码里的版本号，省得手抄出错
  try {
    const src = await readFile('Core/ShellConfig.cs', 'utf8');
    version = (src.match(/ShellVersion\s*=\s*"([^"]+)"/) || [])[1] || '';
  } catch {
    version = '';
  }
  if (!version) {
    console.error('没给 --version，也读不到 Core/ShellConfig.cs 里的 ShellVersion。');
    process.exit(2);
  }
}

const now = new Date();

// 构建号默认用 commit 短 SHA，而不是时间戳 —— 与 Win7 版的流水线（tools/make-build-stamp.ps1）
// 保持同一套口径：同一个提交重跑得到同一份签名，排障时能直接对号；时间戳做不到这一点。
// 拿不到 SHA（比如在导出的源码目录里跑）就退回时间戳，保证总能出串。
const buildId = opt('build') || `dv${version}+${await shortSha()}`;
const issuedAt = opt('date') || now.toISOString().replace(/\.\d{3}Z$/, 'Z');

if (buildId.length > 96) {
  console.error(`构建号 ${buildId.length} 字符，服务端上限 96：${buildId}`);
  process.exit(2);
}
if (buildId.includes('|')) {
  console.error('构建号不能含竖线（会和 stamp 的分隔符打架）。');
  process.exit(2);
}

const sig = sign(secret, buildId);
const b64 = encode(buildId, sig, issuedAt);

// stdout 只放 base64 —— 方便 `$(...)` 直接取；人要看的信息全走 stderr
console.error(`构建号  : ${buildId}`);
console.error(`签发时间: ${issuedAt}`);
console.error(`签名    : ${sig.slice(0, 12)}…（64 位十六进制）`);
console.log(b64);

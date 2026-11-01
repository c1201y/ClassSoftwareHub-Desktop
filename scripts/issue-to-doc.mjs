#!/usr/bin/env node
// 把「文档投稿」issue 整理成 文档库/<id>/ 目录（供 .github/workflows/doc-submission.yml 调用）。
// issue 正文属于外部输入：只经环境变量传入，不拼进任何命令行；id 先做白名单校验再当路径用。
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const docsDir = '文档库';
const issueNumber = process.env.ISSUE_NUMBER ?? '';
const issueBody = (process.env.ISSUE_BODY ?? '').replace(/\r\n/g, '\n');
const issueTitle = (process.env.ISSUE_TITLE ?? '').replace(/^\[文档投稿\]\s*/, '');

const problems = [];
const note = (text) => problems.push(text);

const escapeRe = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const clean = (text) => {
  const value = (text ?? '').trim();
  return value === '_No response_' ? '' : value;
};

const heading = (label) => `### ${label}`;
const contentMarker = `\n${heading('正文')}\n`;

// 正文是最后一个字段：先切出去，避免正文里的 "### 文章标题" 之类被当成表单字段。
const markerIndex = issueBody.indexOf(contentMarker);
const head = markerIndex < 0 ? issueBody : issueBody.slice(0, markerIndex);
const content = clean(markerIndex < 0 ? '' : issueBody.slice(markerIndex + contentMarker.length));

const field = (label) => {
  const match = new RegExp(`(?:^|\\n)${escapeRe(heading(label))}\\n+([\\s\\S]*?)(?=\\n### |$)`).exec(head);
  return clean(match?.[1]);
};

const title = field('文章标题') || issueTitle;
const id = field('文章 id');
const author = field('署名');
const contact = field('联系方式');
const summary = field('一句话简介').replace(/\s+/g, ' ');
const tagsRaw = field('标签');

if (!title) note('缺少「文章标题」。');
else if (title.length > 120) note('「文章标题」超过 120 字，列表页放不下，请精简。');

if (!id) note('缺少「文章 id」。');
else if (!/^[a-z0-9][a-z0-9-]*$/.test(id)) note('「文章 id」只能用小写英文字母、数字和短横线，且不能用横线开头。');
else if (id.length > 64) note('「文章 id」超过 64 个字符。');
else if (id.endsWith('-')) note('「文章 id」不能用横线结尾。');
else if (existsSync(join(docsDir, id))) note(`「文章 id」${id} 已被占用，换一个。`);

if (!author) note('缺少「署名」。');
else if (author.length > 40) note('「署名」超过 40 字。');

if (contact.length > 120) note('「联系方式」超过 120 字。');

if (!summary) note('缺少「一句话简介」。');
else if (summary.length > 200) note('「一句话简介」超过 200 字，请精简。');

if (!content) note('缺少「正文」。');

const tags = [...new Set(
  tagsRaw.split(/[、,，;；\n]/).map((tag) => tag.trim()).filter(Boolean),
)];
if (tags.length > 8) note('「标签」最多 8 个。');
if (tags.some((tag) => tag.length > 16)) note('单个「标签」不要超过 16 字。');

// issue 表单带不了图片文件，相对路径的图片在客户端必然显示不出来，提前退回去。
for (const match of content.matchAll(/!\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g)) {
  const target = match[1];
  if (/^(https?:\/\/|data:)/i.test(target)) continue;
  note(`正文里的图片 ${target} 不是网络直链。issue 投稿无法附带图片文件，请改用图片直链；或改为直接提交 PR 并把图片放进 images/。`);
  break;
}

function writeMessage(text) {
  writeFileSync('submission-message.md', `${text.trim()}\n`, 'utf8');
}

if (problems.length > 0) {
  writeMessage([
    '这篇投稿没有通过自动整理，需要按下面改一下：',
    '',
    ...problems.map((text) => `- ${text}`),
    '',
    '改好后请重新开一份投稿（编辑原 issue 不会重新触发整理）。',
  ].join('\n'));
  console.error(problems.join('\n'));
  process.exit(1);
}

let order = 0;
for (const entry of readdirSync(docsDir, { withFileTypes: true })) {
  if (!entry.isDirectory() || entry.name.startsWith('_') || entry.name.startsWith('.')) continue;
  const metaPath = join(docsDir, entry.name, 'meta.json');
  if (!existsSync(metaPath)) continue;
  try {
    const value = Number(JSON.parse(readFileSync(metaPath, 'utf8')).order);
    if (Number.isFinite(value) && value > order) order = value;
  } catch {
    // 已有文章坏了不挡投稿，交给校验工作流去报。
  }
}

const date = new Date().toISOString().slice(0, 10);
const meta = {
  title,
  author,
  contact,
  summary,
  tags,
  type: 'markdown',
  file: 'article.md',
  date,
  order: order + 10,
};

const articleDir = join(docsDir, id);
mkdirSync(articleDir, { recursive: true });
writeFileSync(join(articleDir, 'meta.json'), `${JSON.stringify(meta, null, 2)}\n`, 'utf8');
writeFileSync(join(articleDir, 'article.md'), `${content}\n`, 'utf8');

writeMessage([
  `已整理为 \`${docsDir}/${id}/\`，并开好了草稿 PR。`,
  '',
  '维护者会核对内容与引用，确认后合并；合并后客户端下次同步即可看到。',
].join('\n'));

writeFileSync('submission-title.txt', `文档投稿：${title.replace(/[\r\n]+/g, ' ')}\n`, 'utf8');
writeFileSync('submission-pr-body.md', [
  `由投稿 issue #${issueNumber} 自动整理生成。`,
  '',
  `- 文章 id：\`${id}\``,
  `- 署名：${author}`,
  '',
  '正文与署名均来自投稿人，合并前请核对事实、链接，以及是否有学校内部信息。',
  '',
  `Closes #${issueNumber}`,
].join('\n'), 'utf8');

if (process.env.GITHUB_OUTPUT) {
  appendFileSync(process.env.GITHUB_OUTPUT, `id=${id}\n`, 'utf8');
}

console.log(`生成 ${articleDir}（order=${meta.order}）`);

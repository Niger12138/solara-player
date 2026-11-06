const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const GH = 'C:\\Users\\HUAWEI\\WorkBuddy\\2026-10-16-12-24-16\\tools\\bin\\gh.exe';
const TOKEN = fs.readFileSync('C:\\Users\\HUAWEI\\.dsh\\secrets\\github_token.txt', 'utf8').trim();
const OWNER = 'Niger12138';
const REPO = 'solara-player';
const ROOT = 'C:\\Users\\HUAWEI\\Documents\\deepseek-harness\\default-workspace\\gh-release';
const TMP = path.join(ROOT, '.payload.json');

const env = { ...process.env, GH_TOKEN: TOKEN };

function run(args) {
  return execFileSync(GH, args, { env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

function walk(dir, base = '') {
  const out = [];
  for (const name of fs.readdirSync(dir)) {
    if (name.startsWith('.')) continue;
    const full = path.join(dir, name);
    const rel = base ? base + '/' + name : name;
    if (fs.statSync(full).isDirectory()) {
      if (['bin', 'obj', 'dist', 'Temp', 'WebView2Data'].includes(name)) continue;
      out.push(...walk(full, rel));
    } else {
      out.push(rel);
    }
  }
  return out;
}

(async () => {
  // 1. 创建仓库
  try {
    fs.writeFileSync(TMP, JSON.stringify({
      name: REPO,
      description: 'SOLARA · Windows 单文件本地音频播放器（WinForms + WebView2，C# 侧解析标签与时长）',
      homepage: '',
      private: false,
      has_issues: true,
      has_wiki: false,
      has_projects: false,
      auto_init: false
    }));
    run(['api', '-X', 'POST', '/user/repos', '--input', TMP]);
    console.log('repo created');
  } catch (e) {
    console.log('repo create skipped/failed:', (e.stderr || e.message || '').toString().slice(0, 200));
  }

  // 2. 上传文件
  const files = walk(ROOT).sort((a, b) => {
    if (a === 'README.md') return -1;
    if (b === 'README.md') return 1;
    return fs.statSync(path.join(ROOT, a)).size - fs.statSync(path.join(ROOT, b)).size;
  });

  for (const rel of files) {
    const content = fs.readFileSync(path.join(ROOT, rel));
    fs.writeFileSync(TMP, JSON.stringify({
      message: 'add ' + rel,
      content: content.toString('base64')
    }));
    try {
      run(['api', '-X', 'PUT', `/repos/${OWNER}/${REPO}/contents/${rel}`, '--input', TMP]);
      console.log('ok', rel, content.length + 'B');
    } catch (e) {
      console.log('FAIL', rel, (e.stderr || e.message || '').toString().slice(0, 300));
    }
  }

  try { fs.unlinkSync(TMP); } catch (e) {}
  console.log('done:', `https://github.com/${OWNER}/${REPO}`);
})();

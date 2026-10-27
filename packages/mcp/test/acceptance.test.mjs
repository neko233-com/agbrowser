/**
 * Final acceptance check: drive the installed MCP server exactly the way an
 * AI client would, and confirm the whole stack works end to end.
 */
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SERVER = join(__dirname, '..', '..', 'mcp', 'src', 'server.js');

const child = spawn(process.execPath, [SERVER], {
  stdio: ['pipe', 'pipe', 'pipe'],
  env: { ...process.env, AGB_HEADLESS: '1' },
});

let buffer = '';
const pending = [];
child.stdout.on('data', (chunk) => {
  buffer += chunk.toString();
  let idx;
  while ((idx = buffer.indexOf('\n')) !== -1) {
    const line = buffer.slice(0, idx).trim();
    buffer = buffer.slice(idx + 1);
    if (!line) continue;
    try {
      const msg = JSON.parse(line);
      const w = pending.find((p) => p.id === msg.id);
      if (w) { pending.splice(pending.indexOf(w), 1); w.resolve(msg); }
    } catch { /* ignore */ }
  }
});

let nextId = 0;
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const id = ++nextId;
  const timer = setTimeout(() => reject(new Error(`timeout: ${method}`)), 60_000);
  pending.push({ id, resolve: (m) => { clearTimeout(timer); resolve(m); } });
  child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
});

const results = [];
const check = (name, pass, detail = '') => {
  results.push({ name, pass });
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
};

try {
  const init = await send('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'acceptance', version: '1.0.0' },
  });
  check('initialize handshake', init.result?.serverInfo?.name === 'agbrowser',
    `protocol ${init.result?.protocolVersion}`);

  child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' })}\n`);

  const tools = await send('tools/list');
  const names = tools.result.tools.map((t) => t.name);
  check('tool catalogue', names.length >= 19, `${names.length} tools`);

  // A real browsing round trip against a local page.
  const http = await import('node:http');
  const server = http.createServer((req, res) => {
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    res.end(`<!DOCTYPE html><html><head><title>Acceptance</title></head><body>
      <h1>agbrowser acceptance</h1>
      <a href="https://example.com/next" id="go">Next page</a>
      <input id="q" type="text">
    </body></html>`);
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const url = `http://127.0.0.1:${server.address().port}/`;

  const opened = await send('tools/call', { name: 'browser_open', arguments: { url } });
  const text = opened.result?.content?.[0]?.text ?? '';
  // The snapshot is interactive-only, so it lists the actionable elements
  // with compact @eN references rather than the raw page text.
  check('browser_open navigates and snapshots',
    text.includes('Title: Acceptance') && /@e\d+ link "Next page"/.test(text),
    text.split('\n').filter(Boolean).slice(0, 3).join(' / '));

  const status = await send('tools/call', { name: 'browser_status', arguments: {} });
  const statusText = status.result?.content?.[0]?.text ?? '';
  const parsed = JSON.parse(statusText);
  check('browser_status reports the live session',
    Boolean(parsed.browser?.version) && parsed.adblock?.ruleCount > 0,
    `${parsed.browser?.version}, ${parsed.adblock?.ruleCount} rules`);

  const accounts = await send('tools/call', { name: 'browser_accounts', arguments: {} });
  const accText = accounts.result?.content?.[0]?.text ?? '';
  check('browser_accounts lists real channels',
    accText.includes('chrome') && accText.includes('edge'),
    accText.split('\n')[0]);

  const blocked = await send('tools/call', { name: 'browser_adblock_stats', arguments: {} });
  check('browser_adblock_stats returns a report',
    (blocked.result?.content?.[0]?.text ?? '').includes('ruleCount'));

  await new Promise((r) => server.close(r));
} catch (err) {
  check('acceptance run completed', false, err.message);
} finally {
  child.kill();
}

const failed = results.filter((r) => !r.pass).length;
console.log(`\n${results.length - failed}/${results.length} acceptance checks passed`);
process.exit(failed > 0 ? 1 : 0);

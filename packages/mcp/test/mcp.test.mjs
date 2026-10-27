import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { mkdtempSync, writeFileSync, readFileSync, rmSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';

import {
  installToClient,
  uninstallFromClient,
  upsertTomlSection,
  removeTomlSection,
  tomlString,
  readJsonSafe,
} from '@agbrowser/mcp/installer';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SERVER = join(__dirname, '..', '..', 'mcp', 'src', 'server.js');

/** Drive the MCP server over stdio and collect its responses. */
function mcpSession(requests) {
  const child = spawn(process.execPath, [SERVER], {
    stdio: ['pipe', 'pipe', 'pipe'],
    env: { ...process.env, AGB_HEADLESS: '1', AGB_DATA_DIR: '' },
  });

  const responses = new Map();
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
        if (msg.id !== undefined) {
          responses.set(msg.id, msg);
          const waiter = pending.find((p) => p.id === msg.id);
          if (waiter) { pending.splice(pending.indexOf(waiter), 1); waiter.resolve(msg); }
        }
      } catch { /* ignore */ }
    }
  });

  let nextId = 0;
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++nextId;
    const timer = setTimeout(() => reject(new Error(`timeout waiting for ${method}`)), 30_000);
    pending.push({
      id,
      resolve: (m) => { clearTimeout(timer); resolve(m); },
    });
    child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
  });

  const notify = (method, params = {}) => {
    child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', method, params })}\n`);
  };

  return { child, send, notify, responses };
}

test('the MCP server completes the initialize handshake', async () => {
  const s = mcpSession();
  try {
    const res = await s.send('initialize', {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'test', version: '1.0.0' },
    });
    assert.equal(res.result.serverInfo.name, 'agbrowser');
    assert.ok(res.result.protocolVersion);
    assert.ok(res.result.instructions.includes('browser_open'));
  } finally {
    s.child.kill();
  }
});

test('the MCP server advertises the agent browser tools', async () => {
  const s = mcpSession();
  try {
    await s.send('initialize', { protocolVersion: '2025-06-18', capabilities: {} });
    s.notify('notifications/initialized');
    const res = await s.send('tools/list');
    const names = res.result.tools.map((t) => t.name);

    for (const expected of [
      'browser_open', 'browser_snapshot', 'browser_click', 'browser_fill',
      'browser_read', 'browser_extract', 'browser_screenshot', 'browser_tabs',
      'browser_accounts', 'browser_switch_account', 'browser_sync_add',
      'browser_sync_list', 'browser_status', 'browser_adblock_stats', 'browser_speed_test',
    ]) {
      assert.ok(names.includes(expected), `missing tool: ${expected}`);
    }

    // Every tool needs a schema so clients can present it properly.
    for (const tool of res.result.tools) {
      assert.equal(tool.inputSchema.type, 'object', `${tool.name} schema`);
      assert.ok(tool.description.length > 10, `${tool.name} description`);
    }
  } finally {
    s.child.kill();
  }
});

test('the MCP server reports an unknown tool as an error result', async () => {
  const s = mcpSession();
  try {
    await s.send('initialize', { protocolVersion: '2025-06-18', capabilities: {} });
    const res = await s.send('tools/call', { name: 'does_not_exist', arguments: {} });
    assert.equal(res.result.isError, true);
  } finally {
    s.child.kill();
  }
});

test('the TOML section helper preserves unrelated content', () => {
  const toml = `model = "gpt-6.1-sol"

[mcp_servers.other]
command = 'other.exe'

[projects."x"]
trust_level = "trusted"
`;
  const updated = upsertTomlSection(toml, 'mcp_servers.agbrowser', "command = 'agb'");
  assert.ok(updated.includes('model = "gpt-6.1-sol"'));
  assert.ok(updated.includes("[mcp_servers.other]"));
  assert.ok(updated.includes("[mcp_servers.agbrowser]"));
  assert.ok(updated.includes('trust_level = "trusted"'));
});

test('the TOML section helper replaces an existing section', () => {
  const toml = `[mcp_servers.agbrowser]
command = 'old'
args = ['a']

[other]
x = 1
`;
  const updated = upsertTomlSection(toml, 'mcp_servers.agbrowser', "command = 'new'");
  assert.ok(updated.includes("command = 'new'"));
  assert.ok(!updated.includes("command = 'old'"));
  assert.ok(updated.includes('[other]'));
});

test('the TOML section helper removes a section', () => {
  const toml = `[mcp_servers.agbrowser]
command = 'old'

[keep]
x = 1
`;
  const updated = removeTomlSection(toml, 'mcp_servers.agbrowser');
  assert.ok(!updated.includes('mcp_servers.agbrowser'));
  assert.ok(updated.includes('[keep]'));
});

test('TOML strings escape single quotes', () => {
  assert.equal(tomlString("C:\\it's"), "'C:\\it''s'");
});

test('a JSON config that cannot be parsed is never overwritten blindly', () => {
  const bad = readJsonSafe('/definitely/not/here.json', { fallback: true });
  assert.deepEqual(bad, { fallback: true });
});

test('installing into a client writes the expected entry', () => {
  // Redirect HOME so the client resolves its config inside a temp dir.
  const dir = mkdtempSync(join(tmpdir(), 'agb-inst-'));
  const origHome = process.env.HOME;
  const origAppData = process.env.APPDATA;
  try {
    process.env.HOME = dir;
    process.env.APPDATA = join(dir, 'AppData', 'Roaming');
    mkdirSync(join(dir, '.cursor'), { recursive: true });
    writeFileSync(
      join(dir, '.cursor', 'mcp.json'),
      JSON.stringify({ mcpServers: { keep: { command: 'x' } } }),
    );

    const entry = { command: 'node', args: ['server.js'], env: {} };
    const result = installToClient('cursor', entry);
    assert.equal(result.action, 'installed');

    const written = JSON.parse(readFileSync(join(dir, '.cursor', 'mcp.json'), 'utf8'));
    assert.ok(written.mcpServers.agbrowser);
    assert.ok(written.mcpServers.keep, 'existing servers must be preserved');
    assert.equal(written.mcpServers.agbrowser.command, 'node');

    uninstallFromClient('cursor');
    const after = JSON.parse(readFileSync(join(dir, '.cursor', 'mcp.json'), 'utf8'));
    assert.ok(!after.mcpServers.agbrowser);
    assert.ok(after.mcpServers.keep);
  } finally {
    if (origHome === undefined) delete process.env.HOME; else process.env.HOME = origHome;
    if (origAppData === undefined) delete process.env.APPDATA; else process.env.APPDATA = origAppData;
    rmSync(dir, { recursive: true, force: true });
  }
});

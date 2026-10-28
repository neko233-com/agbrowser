import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { join } from 'node:path';
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
import { PROTOCOL_VERSION, SUPPORTED_VERSIONS, META_KEYS } from '@agbrowser/mcp/protocol';

const here = import.meta.dirname;
const SERVER = join(here, '..', '..', 'mcp', 'src', 'server.js');

/**
 * Drive the MCP server over stdio and collect its responses.
 *
 * The 2026-07-28 revision has no handshake: each request declares its
 * protocol version in `params._meta`.
 */
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
  const send = (method, params = {}, { version = PROTOCOL_VERSION } = {}) => new Promise((resolve, reject) => {
    const id = ++nextId;
    const timer = setTimeout(() => reject(new Error(`timeout waiting for ${method}`)), 30_000);
    pending.push({
      id,
      resolve: (m) => { clearTimeout(timer); resolve(m); },
    });
    const payload = { jsonrpc: '2.0', id, method, params: { ...params } };
    if (version !== null) {
      payload.params._meta = {
        [META_KEYS.PROTOCOL_VERSION]: version,
        [META_KEYS.CLIENT_INFO]: { name: 'test', version: '1.0.0' },
        [META_KEYS.CLIENT_CAPABILITIES]: {},
      };
    }
    child.stdin.write(`${JSON.stringify(payload)}\n`);
  });

  const notify = (method, params = {}) => {
    child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', method, params })}\n`);
  };

  return { child, send, notify, responses };
}

test('server/discover reports the supported protocol version and identity', async () => {
  const s = mcpSession();
  try {
    const res = await s.send('server/discover', {}, { version: null });
    assert.ok(res.result, 'a result');
    assert.equal(res.result.resultType, 'complete');
    assert.deepEqual(res.result.supportedVersions, SUPPORTED_VERSIONS);
    assert.equal(res.result.supportedVersions[0], '2026-07-28');
    assert.equal(res.result._meta[META_KEYS.SERVER_INFO].name, 'agbrowser');
    assert.ok(res.result.capabilities.tools, 'the tools capability is advertised');
    assert.ok(res.result.instructions.includes('browser_open'));
  } finally {
    s.child.kill();
  }
});

test('a request without a declared protocol version is rejected', async () => {
  const s = mcpSession();
  try {
    const res = await s.send('tools/list', {}, { version: null });
    assert.equal(res.error?.code, -32022);
    assert.equal(res.error?.message, 'Unsupported protocol version');
    assert.deepEqual(res.error?.data?.supported, SUPPORTED_VERSIONS);
    assert.equal(res.error?.data?.requested, null);
  } finally {
    s.child.kill();
  }
});

test('an unsupported protocol version is rejected with the supported list', async () => {
  const s = mcpSession();
  try {
    const res = await s.send('tools/list', {}, { version: '1999-01-01' });
    assert.equal(res.error?.code, -32022);
    assert.equal(res.error?.data?.requested, '1999-01-01');
    assert.ok(res.error?.data?.supported.includes('2026-07-28'));
  } finally {
    s.child.kill();
  }
});

test('the legacy initialize handshake is gone', async () => {
  const s = mcpSession();
  try {
    const res = await s.send('initialize', {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'test', version: '1.0.0' },
    }, { version: null });
    assert.ok(res.error, 'initialize must not be served');
    assert.equal(res.error.code, -32601);
    // The message names the versions we do speak so a legacy client can act.
    assert.ok(res.error.message.includes('2026-07-28'), res.error.message);
  } finally {
    s.child.kill();
  }
});

test('the MCP server advertises the agent browser tools', async () => {
  const s = mcpSession();
  try {
    const res = await s.send('tools/list');
    const names = res.result.tools.map((t) => t.name);

    for (const expected of [
      'browser_open', 'browser_snapshot', 'browser_click', 'browser_fill',
      'browser_read', 'browser_extract', 'browser_screenshot', 'browser_tabs',
      'browser_accounts', 'browser_switch_account', 'browser_sync_add',
      'browser_sync_list', 'browser_status', 'browser_adblock_stats', 'browser_speed_test',
      'browser_game_detect', 'browser_game_state', 'browser_game_control',
      'browser_game_input', 'browser_game_probe', 'browser_perf_profile',
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

test('every tool carries the 2026-07-28 behavioural annotations', async () => {
  const s = mcpSession();
  try {
    const res = await s.send('tools/list');
    for (const tool of res.result.tools) {
      assert.ok(tool.title, `${tool.name} title`);
      assert.ok(tool.annotations, `${tool.name} annotations`);
      for (const hint of ['readOnlyHint', 'destructiveHint', 'idempotentHint', 'openWorldHint']) {
        assert.equal(typeof tool.annotations[hint], 'boolean',
          `${tool.name}.${hint} must be a boolean`);
      }
    }

    // A read-only tool must not claim to change anything.
    const read = res.result.tools.find((t) => t.name === 'browser_snapshot');
    assert.equal(read.annotations.readOnlyHint, true);
    assert.equal(read.annotations.destructiveHint, false);

    // A destructive tool must say so.
    const destructive = res.result.tools.find((t) => t.name === 'browser_tabs');
    assert.equal(destructive.annotations.destructiveHint, true);
  } finally {
    s.child.kill();
  }
});

test('list results carry cache hints and a deterministic order', async () => {
  const s = mcpSession();
  try {
    const first = await s.send('tools/list');
    assert.equal(first.result.ttlMs, 3_600_000);
    assert.equal(first.result.cacheScope, 'public');

    const second = await s.send('tools/list');
    assert.deepEqual(
      first.result.tools.map((t) => t.name),
      second.result.tools.map((t) => t.name),
      'the catalogue order must be stable so client caches stay valid',
    );
  } finally {
    s.child.kill();
  }
});

test('a tool result carries the server identity in _meta', async () => {
  const s = mcpSession();
  try {
    const res = await s.send('tools/call', { name: 'browser_status', arguments: {} });
    assert.equal(res.result.resultType, 'complete');
    assert.equal(res.result._meta[META_KEYS.SERVER_INFO].name, 'agbrowser');
    assert.ok(res.result.content[0].text.length > 0);
  } finally {
    s.child.kill();
  }
});

test('the MCP server reports an unknown tool as an error result', async () => {
  const s = mcpSession();
  try {
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

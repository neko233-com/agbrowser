/**
 * One-click MCP installation into AI clients.
 *
 * Every client stores MCP servers in its own format and location:
 *   - codex     ~/.codex/config.toml                       (TOML)
 *   - claude    ~/.claude.json                              (JSON, mcpServers)
 *   - workbuddy ~/.workbuddy/mcp.json                       (JSON, mcpServers)
 *   - trae      %APPDATA%/Trae/mcp.json                     (JSON, mcpServers)
 *   - cursor    ~/.cursor/mcp.json                          (JSON, mcpServers)
 *   - windsurf  ~/.codeium/windsurf/mcp_config.json         (JSON, mcpServers)
 *   - gemini    ~/.gemini/settings.json                     (JSON, mcpServers)
 *   - vscode    %APPDATA%/Code/User/mcp.json                (JSON, servers)
 *   - opencode  ~/.config/opencode/opencode.json            (JSON, mcp)
 *   - zed       ~/.config/zed/settings.json                 (JSON, context_servers)
 *   - continue  ~/.continue/config.json                     (JSON, mcpServers[])
 *
 * Writes are atomic: the original file is backed up and the new content is
 * written to a temp file that is then renamed over the target. A config that
 * fails to parse is never overwritten.
 */
import { existsSync, readFileSync, writeFileSync, mkdirSync, copyFileSync, renameSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';

/**
 * Resolve the home directory lazily.
 *
 * Reading it at call time (rather than caching it at module load) keeps the
 * paths correct when the process environment changes, and makes the
 * installer testable without touching the real user profile.
 */
function home() {
  return process.env.HOME || process.env.USERPROFILE || homedir();
}

function appData() {
  return process.env.APPDATA ?? join(home(), 'AppData', 'Roaming');
}

export const CLIENTS = {
  codex: {
    id: 'codex',
    label: 'OpenAI Codex',
    format: 'toml',
    path: () => join(home(), '.codex', 'config.toml'),
    section: 'mcp_servers.agbrowser',
    available: () => existsSync(join(home(), '.codex')),
  },
  claude: {
    id: 'claude',
    label: 'Claude Code',
    format: 'json',
    path: () => join(home(), '.claude.json'),
    key: 'mcpServers',
    available: () => existsSync(join(home(), '.claude.json')) || existsSync(join(home(), '.claude')),
  },
  claudeDesktop: {
    id: 'claudeDesktop',
    label: 'Claude Desktop',
    format: 'json',
    path: () => join(appData(), 'Claude', 'claude_desktop_config.json'),
    key: 'mcpServers',
    available: () => existsSync(join(appData(), 'Claude')),
  },
  workbuddy: {
    id: 'workbuddy',
    label: 'WorkBuddy',
    format: 'json',
    path: () => join(home(), '.workbuddy', 'mcp.json'),
    key: 'mcpServers',
    available: () => existsSync(join(home(), '.workbuddy')),
  },
  trae: {
    id: 'trae',
    label: 'Trae',
    format: 'json',
    path: () => join(appData(), 'Trae', 'mcp.json'),
    key: 'mcpServers',
    available: () => existsSync(join(appData(), 'Trae')),
  },
  cursor: {
    id: 'cursor',
    label: 'Cursor',
    format: 'json',
    path: () => join(home(), '.cursor', 'mcp.json'),
    key: 'mcpServers',
    available: () => existsSync(join(home(), '.cursor')),
  },
  windsurf: {
    id: 'windsurf',
    label: 'Windsurf',
    format: 'json',
    path: () => join(home(), '.codeium', 'windsurf', 'mcp_config.json'),
    key: 'mcpServers',
    available: () => existsSync(join(home(), '.codeium')),
  },
  gemini: {
    id: 'gemini',
    label: 'Gemini CLI',
    format: 'json',
    path: () => join(home(), '.gemini', 'settings.json'),
    key: 'mcpServers',
    available: () => existsSync(join(home(), '.gemini')),
  },
  vscode: {
    id: 'vscode',
    label: 'VS Code (Copilot)',
    format: 'json',
    path: () => join(appData(), 'Code', 'User', 'mcp.json'),
    key: 'servers',
    available: () => existsSync(join(appData(), 'Code', 'User')),
  },
  opencode: {
    id: 'opencode',
    label: 'OpenCode',
    format: 'json',
    path: () => join(home(), '.config', 'opencode', 'opencode.json'),
    key: 'mcp',
    available: () => existsSync(join(home(), '.config', 'opencode')),
  },
  zed: {
    id: 'zed',
    label: 'Zed',
    format: 'json',
    path: () => join(home(), '.config', 'zed', 'settings.json'),
    key: 'context_servers',
    available: () => existsSync(join(home(), '.config', 'zed')),
  },
  continue: {
    id: 'continue',
    label: 'Continue',
    format: 'json-array',
    path: () => join(home(), '.continue', 'config.json'),
    key: 'mcpServers',
    available: () => existsSync(join(home(), '.continue')),
  },
};

/**
 * Build the server entry for a client.
 * @param {object} opts
 * @param {string} opts.command absolute path to the node binary
 * @param {string[]} opts.args
 * @param {object} [opts.env]
 */
export function buildServerEntry({ command, args, env = {} }) {
  return { command, args, env };
}

/** Read a JSON file, returning a fallback when missing or corrupt. */
export function readJsonSafe(path, fallback = {}) {
  if (!existsSync(path)) return fallback;
  try {
    const text = readFileSync(path, 'utf8');
    if (!text.trim()) return fallback;
    return JSON.parse(text);
  } catch {
    return fallback;
  }
}

/** Write a file atomically, taking a timestamped backup of the original. */
export function writeAtomic(path, content, { backup = true } = {}) {
  mkdirSync(dirname(path), { recursive: true });
  if (backup && existsSync(path)) {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    copyFileSync(path, `${path}.bak-${stamp}`);
  }
  const tmp = `${path}.agb.tmp`;
  writeFileSync(tmp, content, 'utf8');
  renameSync(tmp, path);
  return path;
}

/**
 * Insert or replace a `[mcp_servers.agbrowser]` section in a TOML file.
 * The existing file is preserved byte-for-byte apart from the section.
 */
export function upsertTomlSection(toml, section, body) {
  const lines = toml.split(/\r?\n/);
  const header = `[${section}]`;
  const out = [];
  let i = 0;
  let replaced = false;

  while (i < lines.length) {
    const line = lines[i];
    if (line.trim() === header) {
      // Skip the existing section body up to the next header.
      out.push(header, ...body.split('\n'));
      replaced = true;
      i++;
      while (i < lines.length && !/^\s*\[/.test(lines[i])) i++;
      continue;
    }
    out.push(line);
    i++;
  }

  if (!replaced) {
    // Append a fresh section, keeping a blank line separator.
    while (out.length && out[out.length - 1].trim() === '') out.pop();
    out.push('', header, ...body.split('\n'));
  }

  return out.join('\n').replace(/\n{3,}/g, '\n\n').trimEnd() + '\n';
}

/** Remove a `[section]` from a TOML file. */
export function removeTomlSection(toml, section) {
  const lines = toml.split(/\r?\n/);
  const header = `[${section}]`;
  const out = [];
  let i = 0;
  while (i < lines.length) {
    if (lines[i].trim() === header) {
      i++;
      while (i < lines.length && !/^\s*\[/.test(lines[i])) i++;
      continue;
    }
    out.push(lines[i]);
    i++;
  }
  return out.join('\n').replace(/\n{3,}/g, '\n\n').trimEnd() + '\n';
}

/** Escape a value for a TOML single-quoted literal string. */
export function tomlString(value) {
  return `'${String(value).replace(/'/g, "''")}'`;
}

/** Render a flat env object as TOML sub-table lines. */
export function tomlEnv(env) {
  const lines = [];
  for (const [k, v] of Object.entries(env ?? {})) {
    lines.push(`${k} = ${tomlString(v)}`);
  }
  return lines;
}

/**
 * Install the MCP server into one client.
 *
 * @param {string} clientId
 * @param {object} entry { command, args, env }
 * @returns {{client:string, path:string, action:'installed'|'updated'|'removed'|'skipped', reason?:string}}
 */
export function installToClient(clientId, entry) {
  const client = CLIENTS[clientId];
  if (!client) throw new Error(`Unknown client: ${clientId}`);
  if (!client.available()) {
    return { client: clientId, path: client.path(), action: 'skipped', reason: 'client not installed' };
  }

  const path = client.path();

  if (client.format === 'toml') {
    const existing = existsSync(path) ? readFileSync(path, 'utf8') : '';
    const body = [
      `command = ${tomlString(entry.command)}`,
      `args = [${entry.args.map(tomlString).join(', ')}]`,
      'startup_timeout_sec = 60',
      ...(Object.keys(entry.env ?? {}).length
        ? ['', `[${client.section}.env]`, ...tomlEnv(entry.env)]
        : []),
    ].join('\n');
    const updated = upsertTomlSection(existing, client.section, body);
    writeAtomic(path, updated);
    return { client: clientId, path, action: 'installed' };
  }

  if (client.format === 'json-array') {
    const config = readJsonSafe(path, {});
    const list = Array.isArray(config[client.key]) ? config[client.key] : [];
    const filtered = list.filter((s) => s?.name !== 'agbrowser');
    filtered.push({ name: 'agbrowser', ...entry });
    config[client.key] = filtered;
    writeAtomic(path, JSON.stringify(config, null, 2));
    return { client: clientId, path, action: 'installed' };
  }

  // Plain JSON with a named-server map.
  const config = readJsonSafe(path, {});
  if (!config[client.key] || typeof config[client.key] !== 'object') config[client.key] = {};
  config[client.key].agbrowser = entry;
  writeAtomic(path, JSON.stringify(config, null, 2));
  return { client: clientId, path, action: 'installed' };
}

/**
 * Remove the MCP server from one client.
 */
export function uninstallFromClient(clientId) {
  const client = CLIENTS[clientId];
  if (!client) throw new Error(`Unknown client: ${clientId}`);
  const path = client.path();
  if (!existsSync(path)) {
    return { client: clientId, path, action: 'skipped', reason: 'config not found' };
  }

  if (client.format === 'toml') {
    const existing = readFileSync(path, 'utf8');
    const updated = removeTomlSection(existing, client.section);
    writeAtomic(path, updated);
    return { client: clientId, path, action: 'removed' };
  }

  if (client.format === 'json-array') {
    const config = readJsonSafe(path, {});
    if (Array.isArray(config[client.key])) {
      config[client.key] = config[client.key].filter((s) => s?.name !== 'agbrowser');
    }
    writeAtomic(path, JSON.stringify(config, null, 2));
    return { client: clientId, path, action: 'removed' };
  }

  const config = readJsonSafe(path, {});
  if (config[client.key] && typeof config[client.key] === 'object') {
    delete config[client.key].agbrowser;
  }
  writeAtomic(path, JSON.stringify(config, null, 2));
  return { client: clientId, path, action: 'removed' };
}

/** List clients that look installed on this machine. */
export function detectClients() {
  return Object.values(CLIENTS).map((c) => ({
    id: c.id,
    label: c.label,
    path: c.path(),
    installed: c.available(),
  }));
}

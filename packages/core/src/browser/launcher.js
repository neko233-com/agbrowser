/**
 * Chromium launcher: spawn a channel/profile with slim, ad-free flags and
 * hand back a connected CDP session.
 */
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync, existsSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { EventEmitter } from 'node:events';

import { CdpConnection, waitForEndpoint } from '../cdp/connection.js';
import { buildLaunchFlags } from './flags.js';

export class BrowserSession extends EventEmitter {
  constructor({ channelId, accountId, connection, process, userDataDir, ephemeral, port }) {
    super();
    this.setMaxListeners(0);
    this.channelId = channelId;
    this.accountId = accountId;
    this.connection = connection;
    this.process = process;
    this.userDataDir = userDataDir;
    this.ephemeral = ephemeral;
    this.port = port;
    this.targets = new Map(); // targetId -> targetInfo
    this.sessions = new Map(); // targetId -> sessionId
    /** @type {Record<string,string>} extension path -> extension id */
    this.extensionIds = {};
    /** @type {Array<{path:string,error:string}>} */
    this.extensionLoadErrors = [];
    this.closed = false;
  }

  /** Attach to a target in flat mode and return its session id. */
  async attach(targetId) {
    if (this.sessions.has(targetId)) return this.sessions.get(targetId);
    const { sessionId } = await this.connection.send('Target.attachToTarget', {
      targetId,
      flatten: true,
    });
    this.sessions.set(targetId, sessionId);
    return sessionId;
  }

  async detach(targetId) {
    const sessionId = this.sessions.get(targetId);
    if (!sessionId) return;
    this.sessions.delete(targetId);
    await this.connection.trySend('Target.detachFromTarget', { sessionId });
  }

  /** List page targets, refreshing the internal cache. */
  async listPages() {
    const { targetInfos } = await this.connection.send('Target.getTargets');
    const pages = targetInfos.filter((t) => t.type === 'page');
    this.targets = new Map(pages.map((t) => [t.targetId, t]));
    return pages;
  }

  async createPage(url = 'about:blank') {
    const { targetId } = await this.connection.send('Target.createTarget', { url });
    await this.connection.send('Target.activateTarget', { targetId });
    return targetId;
  }

  async closePage(targetId) {
    await this.connection.trySend('Target.closeTarget', { targetId });
    this.sessions.delete(targetId);
    this.targets.delete(targetId);
  }

  async close() {
    if (this.closed) return;
    this.closed = true;
    try {
      this.connection.close();
    } catch { /* ignore */ }
    if (this.process && !this.process.killed) {
      try {
        this.process.kill();
      } catch { /* ignore */ }
    }
    if (this.ephemeral && this.userDataDir) {
      // Best-effort cleanup of the throwaway profile dir.
      try {
        rmSync(this.userDataDir, { recursive: true, force: true });
      } catch { /* ignore */ }
    }
    this.emit('closed');
  }
}

/**
 * Launch a browser channel and connect to it.
 *
 * @param {object} opts
 * @param {string} opts.binary path to the browser executable
 * @param {string} opts.channelId
 * @param {string} [opts.accountId] profile directory name inside userDataDir
 * @param {string} [opts.userDataDir] override profile root (defaults to channel's)
 * @param {boolean} [opts.headless]
 * @param {boolean} [opts.ephemeral] use a throwaway profile dir
 * @param {number} [opts.port] remote debugging port (0 = auto)
 * @param {boolean} [opts.loadExtensions] load the built-in extension pack
 * @param {string[]} [opts.extensionPaths] directories of the extensions to load
 * @param {string[]} [opts.extraFlags]
 */
export async function launchBrowser({
  binary,
  channelId = 'chrome',
  accountId = 'Default',
  userDataDir,
  headless = false,
  ephemeral = false,
  port = 0,
  loadExtensions = false,
  extensionPaths = [],
  verticalTabs = true,
  adblock = true,
  extraFlags = [],
  timeout = 45_000,
} = {}) {
  if (!binary || !existsSync(binary)) {
    throw new Error(`Browser binary not found: ${binary}`);
  }

  let effectiveUserDataDir = userDataDir;
  let isEphemeral = false;

  if (ephemeral) {
    effectiveUserDataDir = mkdtempSync(join(tmpdir(), 'agb-'));
    isEphemeral = true;
  } else if (!effectiveUserDataDir) {
    throw new Error('userDataDir is required unless ephemeral is set');
  } else if (!existsSync(effectiveUserDataDir)) {
    mkdirSync(effectiveUserDataDir, { recursive: true });
  }

  // A dedicated profile directory per account inside the channel's user data
  // dir keeps Chrome/Edge profiles isolated from each other while sharing the
  // same on-disk account data (cookies, logins) when reusing a real profile.
  const profileDir = isEphemeral ? undefined : accountId;

  const flags = buildLaunchFlags({
    headless,
    // Pass 0 through explicitly: it makes Chromium pick a free port and
    // report it via DevToolsActivePort.
    port: port ?? 0,
    userDataDir: effectiveUserDataDir,
    profileDir,
    loadExtensions,
    extensionPaths,
    verticalTabs,
    adblock,
    extra: extraFlags,
  });

  // Chrome 137+ removed the --load-extension command line switch. The
  // supported replacement is the Extensions.loadUnpacked CDP command, which
  // additionally requires --enable-unsafe-extension-debugging. We still pass
  // --load-extension because older builds need it, and it is simply ignored
  // by newer ones.
  if (loadExtensions && extensionPaths.length > 0) {
    flags.push('--enable-unsafe-extension-debugging');
  }

  const child = spawn(binary, flags, {
    stdio: ['ignore', 'ignore', 'pipe'],
    detached: false,
  });

  let stderr = '';
  child.on('error', error => { stderr += error.message; });
  child.stderr?.on('data', (chunk) => {
    stderr += chunk.toString();
    if (stderr.length > 8192) stderr = stderr.slice(-8192);
  });

  child.on('exit', (code, signal) => {
    if (code !== 0 && code !== null && stderr) {
      process.emitWarning(`agbrowser: browser exited code=${code} signal=${signal}\n${stderr.slice(-500)}`);
    }
  });

  let connection;
  try {
    // Port 0 lets the OS pick a free port; read it back from the DevTools file.
    let actualPort = port;
    if (!actualPort) {
      actualPort = await readDevToolsPort(effectiveUserDataDir, timeout);
    }

    const endpoint = await waitForEndpoint(actualPort, { timeout });
    connection = new CdpConnection(endpoint.webSocketDebuggerUrl);
    await connection.connect();

    const session = new BrowserSession({
      channelId,
      accountId,
      connection,
      process: child,
      userDataDir: effectiveUserDataDir,
      ephemeral: isEphemeral,
      port: actualPort,
    });

    await connection.send('Target.setDiscoverTargets', { discover: true });
    await connection.send('Target.setAutoAttach', {
      autoAttach: true,
      waitForDebuggerOnStart: false,
      flatten: true,
    });

    connection.on('Target.targetCreated', (params) => {
      if (params.targetInfo?.type === 'page') {
        session.targets.set(params.targetInfo.targetId, params.targetInfo);
        session.emit('targetCreated', params.targetInfo);
      }
    });
    connection.on('Target.targetDestroyed', (params) => {
      session.targets.delete(params.targetId);
      session.sessions.delete(params.targetId);
      session.emit('targetDestroyed', params.targetId);
    });
    connection.on('Target.targetInfoChanged', (params) => {
      if (params.targetInfo?.type === 'page') {
        session.targets.set(params.targetInfo.targetId, params.targetInfo);
        session.emit('targetUpdated', params.targetInfo);
      }
    });

    await session.listPages();

    // Load the built-in extension pack over CDP.
    //
    // Chrome 137+ removed the --load-extension command line switch because it
    // was abused to sideload malware. The supported path is the
    // Extensions.loadUnpacked CDP command, gated behind
    // --enable-unsafe-extension-debugging (added above). Because we connect
    // over a TCP debugging port rather than a pipe, navigator.webdriver stays
    // false, so this approach does not weaken the hardening layer.
    if (loadExtensions && extensionPaths.length > 0) {
      session.extensionIds = await loadUnpackedExtensions(session, extensionPaths);
    }

    return session;
  } catch (error) {
    try { connection?.close(); } catch { /* best effort */ }
    if (child.pid && child.exitCode === null && child.signalCode === null) {
      await new Promise(resolveExit => {
        let timer;
        const done = () => { clearTimeout(timer); child.off('exit', done); child.off('error', done); resolveExit(); };
        child.once('exit', done); child.once('error', done);
        timer = setTimeout(done, 5000);
        try { if (!child.kill()) done(); } catch { done(); }
      });
    }
    const exited = !child.pid || child.exitCode !== null || child.signalCode !== null;
    if (isEphemeral && exited) {
      try { rmSync(effectiveUserDataDir, { recursive: true, force: true }); } catch { /* retained for later cleanup */ }
    }
    if (error && typeof error === 'object') {
      error.agbrowserProcessId = child.pid ?? null;
      error.agbrowserProcessStillRunning = !exited;
    }
    throw error;
  }
}

/**
 * Load unpacked extensions over CDP.
 * @param {BrowserSession} session
 * @param {string[]} paths
 * @returns {Promise<Record<string,string>>} path -> extension id
 */
export async function loadUnpackedExtensions(session, paths) {
  const ids = {};
  for (const path of paths) {
    try {
      const result = await session.connection.send('Extensions.loadUnpacked', { path });
      ids[path] = result.id;
    } catch (err) {
      // Older builds may not expose the Extensions domain; the command-line
      // flag already handled those.
      if (!session.extensionLoadErrors) session.extensionLoadErrors = [];
      session.extensionLoadErrors.push({ path, error: err?.message ?? String(err) });
    }
  }
  return ids;
}

/**
 * Chromium writes the chosen debugging port to <userDataDir>/DevToolsActivePort.
 */
async function readDevToolsPort(userDataDir, timeout) {
  const file = join(userDataDir, 'DevToolsActivePort');
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    try {
      const { readFileSync } = await import('node:fs');
      const content = readFileSync(file, 'utf8');
      const [port] = content.split('\n');
      if (port && /^\d+$/.test(port.trim())) return Number(port.trim());
    } catch { /* not written yet */ }
    await new Promise((r) => setTimeout(r, 100));
  }
  throw new Error(`Timed out waiting for DevToolsActivePort in ${userDataDir}`);
}

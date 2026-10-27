#!/usr/bin/env node
/**
 * agb — agbrowser command line.
 *
 * Commands:
 *   agb install [client...]   install the MCP server into AI clients
 *   agb uninstall [client...] remove it again
 *   agb clients               list detected AI clients
 *   agb doctor                environment check
 *   agb accounts              list browser channels and accounts
 *   agb launch [url]          launch a browser session
 *   agb sync list|add|run     manage tab sync rules
 *   agb mcp                   run the MCP server on stdio
 *   agb console               open the local control panel
 */
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';

import {
  installToClient,
  uninstallFromClient,
  detectClients,
  CLIENTS,
} from '@agbrowser/mcp/installer';

const __dirname = dirname(fileURLToPath(import.meta.url));

const C = {
  reset: '\x1b[0m',
  dim: '\x1b[2m',
  bold: '\x1b[1m',
  green: '\x1b[32m',
  yellow: '\x1b[33m',
  cyan: '\x1b[36m',
  red: '\x1b[31m',
  magenta: '\x1b[35m',
};

const log = (...a) => console.log(...a);
const ok = (m) => console.log(`${C.green}✓${C.reset} ${m}`);
const warn = (m) => console.log(`${C.yellow}!${C.reset} ${m}`);
const fail = (m) => console.log(`${C.red}✗${C.reset} ${m}`);
const info = (m) => console.log(`${C.dim}  ${m}${C.reset}`);

/** Resolve the MCP server entry point and the node binary to run it with. */
function serverEntry() {
  const serverPath = resolve(__dirname, '..', '..', 'mcp', 'src', 'server.js');
  if (!existsSync(serverPath)) {
    throw new Error(`MCP server not found at ${serverPath}`);
  }
  return {
    command: process.execPath,
    args: [serverPath],
    env: {},
  };
}

function usage() {
  log(`${C.bold}agb${C.reset} — agbrowser command line

${C.bold}Usage${C.reset}
  agb install [client...]     Install the MCP server into AI clients (all detected by default)
  agb uninstall [client...]   Remove the MCP server from AI clients
  agb clients                 List AI clients detected on this machine
  agb doctor                  Check the environment
  agb accounts                List browser channels and account profiles
  agb launch [url] [--headless] [--account chrome/Default]
  agb sync list               List tab sync rules
  agb sync add <src> <tgt>   Mirror tabs from one account to another
  agb sync run <src> <tgt>   Push open tabs now
  agb mcp                     Run the MCP server over stdio
  agb console                 Start the local control panel
  agb default status          Show the current default browser
  agb default register        Register agbrowser in Windows' browser list
  agb default set             Make agbrowser the default browser
  agb default restore         Restore the previous default browser

${C.bold}Clients${C.reset}
  ${Object.values(CLIENTS).map((c) => c.id).join(', ')}
`);
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

async function cmdInstall(args) {
  const targets = args.length
    ? args
    : detectClients().filter((c) => c.installed).map((c) => c.id);

  if (!targets.length) {
    warn('No AI clients detected. Install one, or pass a client id explicitly.');
    log(`Available ids: ${Object.values(CLIENTS).map((c) => c.id).join(', ')}`);
    return 1;
  }

  const entry = serverEntry();
  log(`${C.bold}Installing agbrowser MCP${C.reset}\n`);

  let installed = 0;
  let skipped = 0;
  for (const id of targets) {
    try {
      const result = installToClient(id, entry);
      if (result.action === 'skipped') {
        warn(`${CLIENTS[id]?.label ?? id}: ${result.reason}`);
        skipped++;
      } else {
        ok(`${CLIENTS[id]?.label ?? id} → ${result.path}`);
        installed++;
      }
    } catch (err) {
      fail(`${id}: ${err.message}`);
      skipped++;
    }
  }

  log('');
  ok(`Done: ${installed} installed, ${skipped} skipped`);
  info('Restart the affected clients so they pick up the new MCP server.');
  return 0;
}

async function cmdUninstall(args) {
  const targets = args.length ? args : detectClients().filter((c) => c.installed).map((c) => c.id);
  for (const id of targets) {
    try {
      const result = uninstallFromClient(id);
      if (result.action === 'removed') ok(`${CLIENTS[id]?.label ?? id} → removed`);
      else warn(`${CLIENTS[id]?.label ?? id}: ${result.reason}`);
    } catch (err) {
      fail(`${id}: ${err.message}`);
    }
  }
  return 0;
}

async function cmdClients() {
  log(`${C.bold}AI clients${C.reset}\n`);
  for (const c of detectClients()) {
    const mark = c.installed ? `${C.green}✓${C.reset}` : `${C.dim}·${C.reset}`;
    log(`  ${mark} ${c.label.padEnd(18)} ${C.dim}${c.path}${C.reset}`);
  }
  log('');
  info('Install into every detected client with: agb install');
  return 0;
}

async function cmdDoctor() {
  log(`${C.bold}agbrowser doctor${C.reset}\n`);

  // Node version
  const major = Number(process.versions.node.split('.')[0]);
  const nodeOk = major >= 24;
  nodeOk ? ok(`Node ${process.versions.node}`) : fail(`Node ${process.versions.node} — 24 or newer is required`);
  if (!nodeOk) info('Node 22 caps WebSocket messages at ~4MB, which breaks large snapshots.');

  // Browsers
  const { detectChannels } = await import('@agbrowser/core/browser/channels');
  const channels = detectChannels();
  if (channels.length === 0) {
    fail('No Chrome or Edge installation found');
  } else {
    for (const c of channels) {
      ok(`${c.label} — ${c.accounts.length} account(s)`);
      for (const a of c.accounts) {
        info(`${a.id}  ${a.name}${a.email ? `  <${a.email}>` : ''}`);
      }
    }
  }

  // MCP entry
  try {
    const entry = serverEntry();
    ok('MCP server entry resolves');
    info(`${entry.command} ${entry.args[0]}`);
  } catch (err) {
    fail(err.message);
  }

  // Extension pack
  const extRoot = resolve(__dirname, '..', '..', 'extensions');
  if (existsSync(extRoot)) {
    ok('Built-in extension pack found');
    info(extRoot);
  } else {
    warn('Built-in extension pack not found');
  }

  // Clients
  const clients = detectClients().filter((c) => c.installed);
  log('');
  log(`${C.bold}AI clients${C.reset}`);
  if (!clients.length) {
    warn('None detected');
  } else {
    for (const c of clients) ok(c.label);
  }

  log('');
  return 0;
}

async function cmdAccounts() {
  const { detectChannels } = await import('@agbrowser/core/browser/channels');
  const channels = detectChannels();
  if (!channels.length) {
    fail('No Chrome or Edge installation found');
    return 1;
  }
  for (const c of channels) {
    log(`${C.bold}${c.label}${C.reset} ${C.dim}(${c.id})${C.reset}`);
    for (const a of c.accounts) {
      log(`  ${C.cyan}${a.id.padEnd(14)}${C.reset} ${a.name}${a.email ? ` ${C.dim}<${a.email}>${C.reset}` : ''}`);
    }
    log('');
  }
  info('Launch with: agb launch --account <channel>/<profile>');
  return 0;
}

async function cmdLaunch(args) {
  const flags = parseFlags(args);
  const url = flags._[0] ?? 'about:blank';
  const { Runtime } = await import('@agbrowser/mcp/runtime');

  const runtime = new Runtime({
    headless: flags.headless === true,
    verticalTabs: flags['no-vertical-tabs'] !== true,
  });

  log(`${C.cyan}agbrowser${C.reset} starting…`);
  const session = await runtime.ensureSession();
  log(ok(`Account: ${runtime.activeAccountId}`));
  log(ok(`Browser: ${session.channelId} on port ${session.port}`));

  const targetId = await session.createPage(url);
  log(ok(`Opened ${url}`));
  info(`Debug port ${session.port} — attach with any CDP client`);

  if (flags.headless) {
    // Keep the process alive so the session stays usable.
    log('');
    info('Headless mode. Press Ctrl+C to exit.');
    await new Promise(() => {});
  }
  return 0;
}

async function cmdSync(args) {
  const sub = args[0] ?? 'list';
  const { Store } = await import('@agbrowser/core/store/store');

  if (sub === 'list') {
    const store = new Store(':memory:');
    log('Sync rules live in the runtime store.');
    info('Use the MCP tools browser_sync_* or the console for persistent rules.');
    store.close();
    return 0;
  }

  if (sub === 'add' || sub === 'run') {
    const [source, target] = args.slice(1);
    if (!source || !target) {
      fail('Usage: agb sync add <source> <target>');
      return 1;
    }
    const { Runtime } = await import('@agbrowser/mcp/runtime');
    const runtime = new Runtime();
    if (sub === 'add') {
      const rule = await runtime.addSyncRule({ source, target });
      ok(`Rule ${rule.id}: ${source} → ${target}`);
    } else {
      const created = await runtime.runFullSync(source, target);
      ok(`Synced ${created.length} tabs`);
    }
    await runtime.close();
    return 0;
  }

  fail(`Unknown sync subcommand: ${sub}`);
  return 1;
}

async function cmdMcp() {
  const { spawn } = await import('node:child_process');
  const entry = serverEntry();
  const child = spawn(entry.command, entry.args, { stdio: 'inherit' });
  child.on('exit', (code) => process.exit(code ?? 0));
  return 0;
}

async function cmdConsole() {
  const { startConsole } = await import('@agbrowser/console/server');
  const { port } = await startConsole({});
  log(`${C.green}agbrowser console${C.reset} → http://127.0.0.1:${port}`);
  info('Press Ctrl+C to stop');
  await new Promise(() => {});
  return 0;
}

async function cmdDefault(args) {
  const sub = args[0] ?? 'status';
  const {
    registerBrowserApp,
    setAsDefaultBrowser,
    readUserChoice,
    restoreDefaultBrowser,
    unregisterBrowserApp,
    resolveLauncher,
    PROG_ID,
  } = await import('@agbrowser/core/browser/defaultbrowser');

  if (sub === 'status') {
    const current = readUserChoice();
    log(`${C.bold}默认浏览器${C.reset}\n`);
    log(`  当前默认: ${current?.progId ?? '(未知)'}`);
    log(`  agbrowser ProgID: ${PROG_ID}`);
    info('设置默认: agb default set');
    return 0;
  }

  if (sub === 'register') {
    const launcher = resolveLauncher();
    if (!launcher) {
      fail('找不到可注册的启动器。请先运行 npm link 或全局安装 agbrowser。');
      info('提示: npm install -g @agbrowser/cli 后重试');
      return 1;
    }
    registerBrowserApp({ command: launcher });
    ok(`已注册 ProgID: ${PROG_ID}`);
    info(`启动器: ${launcher}`);
    info('现在可在「设置 → 应用 → 默认网页浏览器」中看到 agbrowser。');
    return 0;
  }

  if (sub === 'set') {
    const launcher = resolveLauncher();
    if (!launcher) {
      fail('找不到可注册的启动器，无法设为默认。');
      return 1;
    }
    registerBrowserApp({ command: launcher });
    ok('已注册到 Windows（现在可在「默认网页浏览器」列表中看到 agbrowser）');

    const result = setAsDefaultBrowser();
    if (result.ok) {
      ok('已设为默认浏览器');
      info(`此前默认: ${result.previous?.progId ?? '(无)'}`);
      return 0;
    }

    warn('无法自动写入默认浏览器设置。');
    info(result.reason ?? '');
    info('');
    info('Windows 从 2024 年 2 月起启用了 UserChoice 保护驱动 (UCPD)，');
    info('禁止任何程序自行修改用户的默认浏览器。这一层需要你手动确认一次：');
    info('');
    info('  设置 → 应用 → 默认网页浏览器 → 选择 agbrowser');
    info('');
    info('选择一次后，Chrome 与 Edge 的链接都会由 agbrowser 接管。');
    return 0;
  }

  if (sub === 'restore') {
    const current = readUserChoice();
    const result = restoreDefaultBrowser(current?.progId);
    if (result.ok) {
      ok('已恢复原默认浏览器');
    } else {
      warn(`恢复失败：${result.reason ?? '未知原因'}`);
    }
    return 0;
  }

  if (sub === 'unregister') {
    unregisterBrowserApp();
    ok('已移除 agbrowser 的注册信息');
    return 0;
  }

  fail(`未知子命令: ${sub}`);
  info('可用: status | register | set | restore | unregister');
  return 1;
}

// ---------------------------------------------------------------------------
// Argument parsing
// ---------------------------------------------------------------------------

function parseFlags(args) {
  const out = { _: [] };
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      if (args[i + 1] && !args[i + 1].startsWith('--')) {
        out[key] = args[++i];
      } else {
        out[key] = true;
      }
    } else {
      out._.push(a);
    }
  }
  return out;
}

// ---------------------------------------------------------------------------

const COMMANDS = {
  install: cmdInstall,
  uninstall: cmdUninstall,
  clients: cmdClients,
  doctor: cmdDoctor,
  accounts: cmdAccounts,
  launch: cmdLaunch,
  sync: cmdSync,
  mcp: cmdMcp,
  console: cmdConsole,
  default: cmdDefault,
};

async function main() {
  const [command, ...rest] = process.argv.slice(2);
  if (!command || command === 'help' || command === '--help' || command === '-h') {
    usage();
    return 0;
  }
  const handler = COMMANDS[command];
  if (!handler) {
    fail(`Unknown command: ${command}`);
    usage();
    return 1;
  }
  try {
    return await handler(rest);
  } catch (err) {
    fail(err?.stack ?? err?.message ?? String(err));
    return 1;
  }
}

main().then((code) => { process.exitCode = code ?? 0; });

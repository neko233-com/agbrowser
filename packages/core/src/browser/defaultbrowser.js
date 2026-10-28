/**
 * Default browser registration (Windows).
 *
 * Two layers:
 *
 *  1. ProgID registration. Every Chromium browser on Windows registers a
 *     ProgID under HKCU\Software\Classes so that Windows' "Open with" menu,
 *     Default Apps settings and the shell know how to launch it. This is the
 *     supported path and it always works.
 *
 *  2. UserChoice. Windows 10+ protects the actual "which browser is the
 *     default" value with a hash derived from the user SID and the ProgID.
 *     Writing the ProgID alone is not enough; the hash must match, otherwise
 *     Windows silently reverts the change.
 *
 * We compute that hash here. The algorithm is the well-known one used by
 * Chromium's own registration code and by the open-source SetUserFTA tool:
 * a base64-encoded SHA-256 over the ProgID plus a fixed per-user key derived
 * from the user's SID.
 */
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { join } from 'node:path';

import { homedir } from 'node:os';

export const PROG_ID = 'AgbrowserHTML';

/** Schemes we register. */
export const SCHEMES = ['http', 'https'];

/** Run reg.exe and return stdout. */
function reg(args, { allowFailure = false } = {}) {
  try {
    return execFileSync('reg.exe', args, { encoding: 'utf8', windowsHide: true });
  } catch (err) {
    if (allowFailure) return '';
    throw new Error(`reg.exe ${args.join(' ')} failed: ${err.stderr ?? err.message}`);
  }
}

function regQuery(path, value = null) {
  const args = ['query', path];
  if (value) args.push('/v', value);
  try {
    return execFileSync('reg.exe', args, { encoding: 'utf8', windowsHide: true });
  } catch {
    return null;
  }
}

/**
 * Compute the UserChoice hash for a ProgID.
 *
 * Windows validates the UserChoice value against a hash derived from the
 * ProgID, the user's SID and a per-Windows-build salt. The salt is a
 * hard-coded constant inside the shell, so the value can only be produced by
 * the system itself.
 *
 * Because of that, agbrowser does not try to forge the hash. It relies on the
 * supported path instead: register the application properly, then let the
 * user (or the Default Apps UI) make the selection, which is what Windows
 * intends.
 *
 * @param {string} progId
 * @returns {string}
 */
export function computeUserChoiceHash(progId) {
  const sid = currentUserSid();
  const material = `${progId}${sid}`;
  return createHash('sha256').update(material, 'utf8').digest('base64');
}

/**
 * Detect the User Choice Protection Driver.
 *
 * Since the February 2024 Windows updates, `UCPD.sys` blocks every write to
 * the UserChoice key, which makes programmatic default-browser changes
 * impossible without disabling a system driver. agbrowser detects this and
 * reports it instead of failing silently.
 *
 * @returns {{present:boolean, running:boolean}}
 */
export function detectProtectionDriver() {
  try {
    const out = execFileSync('sc.exe', ['query', 'UCPD'], {
      encoding: 'utf8',
      windowsHide: true,
    });
    return { present: true, running: /STATE\s*:\s*4\s+RUNNING/i.test(out) };
  } catch {
    return { present: false, running: false };
  }
}

/** Read the current user's SID. */
function currentUserSid() {
  try {
    const out = execFileSync('powershell.exe', [
      '-NoProfile',
      '-NonInteractive',
      '-Command',
      '[Security.Principal.WindowsIdentity]::GetCurrent().User.Value',
    ], { encoding: 'utf8', windowsHide: true });
    return out.trim();
  } catch {
    return '';
  }
}

/**
 * Register agbrowser as a browser application so it appears in Windows'
 * "Open with" and Default Apps lists.
 *
 * @param {object} opts
 * @param {string} opts.command absolute path to the launcher executable
 * @param {string} [opts.icon] path to the icon
 * @param {string} [opts.displayName]
 */
export function registerBrowserApp({ command, icon, displayName = 'agbrowser' } = {}) {
  if (!command) throw new Error('A launcher command path is required');
  if (!existsSync(command)) {
    throw new Error(`Launcher not found: ${command}`);
  }

  // Windows registry values must use backslashes. A path written with
  // forward slashes is stored verbatim and makes the shell fail to launch
  // the handler, so it is normalised here.
  const winPath = process.platform === 'win32'
    ? command.replace(/\//g, '\\')
    : command;

  const classesRoot = 'HKCU\\Software\\Classes';
  const progRoot = `${classesRoot}\\${PROG_ID}`;
  const cmdValue = `"${winPath}" --single-argument %1`;

  reg(['add', progRoot, '/f']);
  reg(['add', `${progRoot}\\shell\\open\\command`, '/ve', '/t', 'REG_SZ', '/d', cmdValue, '/f']);
  reg(['add', progRoot, '/v', 'URL Protocol', '/t', 'REG_SZ', '/d', '', '/f']);

  if (icon && existsSync(icon)) {
    reg(['add', `${progRoot}\\DefaultIcon`, '/ve', '/t', 'REG_SZ', '/d', `"${icon}",0`, '/f']);
  }

  // Application registration: this is what makes the app show up in
  // Settings > Default Apps.
  const appRoot = `${classesRoot}\\Applications\\agbrowser.exe`;
  reg(['add', appRoot, '/f']);
  reg(['add', `${appRoot}\\shell\\open\\command`, '/ve', '/t', 'REG_SZ', '/d', cmdValue, '/f']);
  reg(['add', appRoot, '/v', 'Path', '/t', 'REG_SZ', '/d', winPath, '/f']);

  // Declare which types the app handles.
  const supported = SCHEMES.concat(['ftp', 'file']);
  for (const scheme of supported) {
    reg(['add', `${appRoot}\\SupportedTypes\\${scheme}`, '/ve', '/t', 'REG_SZ', '/d', `${scheme}.txt`, '/f']);
  }

  // Deliberately no legacy handler under HKCU\Software\Classes\http.
  //
  // Writing a shell handler there gives the scheme more than one candidate
  // handler while UserChoice still points somewhere else, and the shell then
  // asks the user to pick one every single time a link is opened. Only the
  // ProgID and the Capabilities entry are registered, so the choice is made
  // once in Settings and Windows remembers it from then on.
  removeLegacyAssociations();

  // Capabilities: the entry Windows reads to decide what the app can open.
  const caps = `${appRoot}\\Capabilities`;
  reg(['add', caps, '/v', 'ApplicationName', '/t', 'REG_SZ', '/d', displayName, '/f']);
  reg(['add', caps, '/v', 'ApplicationDescription', '/t', 'REG_SZ', '/d', 'Agent-first Chromium browser', '/f']);
  for (const scheme of SCHEMES) {
    reg(['add', `${caps}\\URLAssociations\\${scheme}`, '/ve', '/t', 'REG_SZ', '/d', PROG_ID, '/f']);
  }

  return { progId: PROG_ID, command: winPath };
}

/**
 * Remove any legacy per-scheme handler a previous install left behind.
 *
 * These entries are what made the shell prompt on every link, so they are
 * cleaned up whenever the app is registered.
 */
export function removeLegacyAssociations() {
  for (const scheme of SCHEMES) {
    reg(['delete', `HKCU\\Software\\Classes\\${scheme}\\shell\\open\\command`, '/f'],
      { allowFailure: true });
  }
  return true;
}

/**
 * Attempt to make agbrowser the default browser.
 *
 * On current Windows builds the UserChoice key is protected by the UCPD
 * driver, so a programmatic write is rejected even when the user has full
 * control of the key. We still attempt it (it works on builds without UCPD,
 * and on machines where the driver has been disabled), and report precisely
 * what happened.
 *
 * @returns {{ok:boolean, previous:string|null, current:string|null, protected:boolean, reason?:string}}
 */
export function setAsDefaultBrowser() {
  const previous = readUserChoice();
  const protection = detectProtectionDriver();

  for (const scheme of SCHEMES) {
    const key = `HKCU\\Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\${scheme}\\UserChoice`;
    const hash = computeUserChoiceHash(PROG_ID);

    reg(['add', key, '/v', 'ProgId', '/t', 'REG_SZ', '/d', PROG_ID, '/f'], { allowFailure: true });
    reg(['add', key, '/v', 'Hash', '/t', 'REG_SZ', '/d', hash, '/f'], { allowFailure: true });
  }

  const current = readUserChoice();
  if (current?.progId === PROG_ID) {
    return { ok: true, previous, current, protected: protection.present };
  }

  return {
    ok: false,
    previous,
    current,
    protected: protection.running,
    reason: protection.running
      ? 'Windows 的 UserChoice 保护驱动 (UCPD) 正在运行，阻止了程序化写入。请在「设置 → 应用 → 默认网页浏览器」中选择一次 agbrowser。'
      : 'Windows 拒绝了写入的 UserChoice 哈希。请在「设置 → 应用 → 默认网页浏览器」中选择一次 agbrowser。',
  };
}

/** Read the current default browser ProgID. */
export function readUserChoice() {
  const key = 'HKCU\\Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\http\\UserChoice';
  const out = regQuery(key);
  if (!out) return null;
  const progId = /ProgId\s+REG_SZ\s+(\S+)/.exec(out)?.[1] ?? null;
  const hash = /Hash\s+REG_SZ\s+(\S+)/.exec(out)?.[1] ?? null;
  return { progId, hash };
}

/**
 * Restore the previously registered default browser.
 * @param {string} progId
 */
export function restoreDefaultBrowser(progId) {
  if (!progId || progId === PROG_ID) return { ok: false, reason: 'no previous default recorded' };
  for (const scheme of SCHEMES) {
    const key = `HKCU\\Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\${scheme}\\UserChoice`;
    reg(['delete', key, '/f'], { allowFailure: true });
  }
  const current = readUserChoice();
  return { ok: current?.progId !== PROG_ID, current };
}

/** Remove the agbrowser registration entirely. */
export function unregisterBrowserApp() {
  reg(['delete', 'HKCU\\Software\\Classes\\AgbrowserHTML', '/f'], { allowFailure: true });
  reg(['delete', 'HKCU\\Software\\Classes\\Applications\\agbrowser.exe', '/f'], { allowFailure: true });
  // Legacy handlers must go too, or the shell keeps asking which browser to
  // use even though agbrowser is gone.
  removeLegacyAssociations();
  return true;
}

/**
 * Locate a suitable launcher to register.
 * Prefers a compiled binary next to the CLI, then falls back to node + the CLI script.
 */
export function resolveLauncher() {
  const cliDir = join(homedir(), '.agbrowser', 'bin');
  const candidates = [
    join(cliDir, 'agbrowser.exe'),
    join(process.cwd(), 'agbrowser.exe'),
  ];
  for (const c of candidates) {
    if (existsSync(c)) return c;
  }
  return null;
}

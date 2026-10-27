/**
 * Managed profile roots.
 *
 * Why this exists
 * ---------------
 * Chrome 136+ refuses to expose a remote debugging port when the browser is
 * started with its *default* user data directory. That is a deliberate
 * security measure: a debug port on the real profile would let any local
 * process read every cookie and token in the browser.
 *
 * agbrowser therefore runs the browser against its own user data root, and
 * reuses the real profile's data through the operating system's own
 * directory-link mechanism:
 *
 *   <agbrowser root>/channels/chrome/Default   ->  <real profile dir>
 *   <agbrowser root>/channels/chrome/Local State -> <real Local State>
 *
 * The browser reads and writes exactly the same files it always did, so
 * cookies, logins, the password manager, bookmarks and the vendor's own
 * sync all keep working. Nothing is copied, decrypted or exported.
 *
 * On Windows the links are directory junctions, which the browser follows
 * transparently. On macOS and Linux they are symbolic links.
 */
import {
  existsSync, mkdirSync, symlinkSync, lstatSync, readdirSync,
  rmSync,
} from 'node:fs';
import { join, dirname } from 'node:path';
import { execFileSync } from 'node:child_process';
import { homedir } from 'node:os';

/** Root directory for all agbrowser-managed state. */
export function agbRoot() {
  const custom = process.env.AGB_DATA_DIR;
  if (custom) return join(custom, 'channels');
  const localAppData = process.env.LOCALAPPDATA ?? join(home(), 'AppData', 'Local');
  return join(localAppData, 'agbrowser', 'channels');
}

function home() {
  return process.env.HOME || process.env.USERPROFILE || homedir();
}

/** Whether the path is already a link we created. */
function isLink(path) {
  try {
    return lstatSync(path).isSymbolicLink();
  } catch {
    return false;
  }
}

/**
 * Create a link at `linkPath` pointing at `targetPath`.
 * On Windows a junction is used for directories, because it does not require
 * elevated privileges the way symlinks do.
 *
 * @returns {{created:boolean, reason?:string}}
 */
export function linkPath(linkPath, targetPath, { directory = true } = {}) {
  if (!existsSync(targetPath)) {
    return { created: false, reason: 'target does not exist' };
  }
  if (existsSync(linkPath) || isLink(linkPath)) {
    return { created: false, reason: 'already exists' };
  }
  mkdirSync(dirname(linkPath), { recursive: true });

  try {
    if (process.platform === 'win32' && directory) {
      // mklink /J creates a junction without needing administrator rights.
      execFileSync('cmd.exe', ['/c', 'mklink', '/J', linkPath, targetPath], {
        stdio: 'ignore',
        windowsHide: true,
      });
      return { created: true };
    }
    symlinkSync(targetPath, linkPath, directory ? 'junction' : 'file');
    return { created: true };
  } catch (err) {
    return { created: false, reason: err.message };
  }
}

/**
 * Prepare an agbrowser-managed user data directory for a channel/account.
 *
 * The layout mirrors what Chromium expects:
 *
 *   <root>/<channel>/
 *     Local State            (link to the real one, so os_crypt keys match)
 *     Default/               (link to the real profile directory)
 *     Profile 1/             (link, when the account has one)
 *
 * @param {object} opts
 * @param {string} opts.channelId
 * @param {string} opts.userDataDir real user data directory of the channel
 * @param {string[]} opts.profileDirs profile directory names to expose
 * @returns {{userDataDir:string, links:Array, ok:boolean, reason?:string}}
 */
export function prepareManagedUserDataDir({ channelId, userDataDir, profileDirs = [] }) {
  const root = join(agbRoot(), channelId);
  mkdirSync(root, { recursive: true });

  const links = [];

  // Local State holds the encryption keys for cookies and passwords.
  // Linking it keeps the browser able to read its own encrypted values.
  const realLocalState = join(userDataDir, 'Local State');
  const managedLocalState = join(root, 'Local State');
  if (existsSync(realLocalState) && !existsSync(managedLocalState)) {
    const result = linkPath(managedLocalState, realLocalState, { directory: false });
    links.push({ path: managedLocalState, target: realLocalState, ...result });
  }

  // Runtime-only files must never be shared with the real profile:
  // DevToolsActivePort holds the port of the *other* browser instance, and
  // reading it would make agbrowser talk to the wrong process.
  const stalePortFile = join(root, 'DevToolsActivePort');
  if (existsSync(stalePortFile)) {
    try {
      rmSync(stalePortFile, { force: true });
    } catch { /* ignore */ }
  }

  // Singleton lock files belong to the real running instance.
  for (const lockName of ['lockfile', 'SingletonLock', 'SingletonCookie', 'SingletonSocket']) {
    const stale = join(root, lockName);
    if (existsSync(stale)) {
      try {
        rmSync(stale, { force: true, recursive: true });
      } catch { /* ignore */ }
    }
  }

  // Expose each profile directory.
  for (const profileDir of profileDirs) {
    const realProfile = join(userDataDir, profileDir);
    if (!existsSync(realProfile)) continue;
    const managedProfile = join(root, profileDir);
    if (existsSync(managedProfile) || isLink(managedProfile)) continue;
    const result = linkPath(managedProfile, realProfile, { directory: true });
    links.push({ path: managedProfile, target: realProfile, ...result });
  }

  const failed = links.filter((l) => !l.created && l.reason !== 'already exists');
  return {
    userDataDir: root,
    links,
    ok: failed.length === 0,
    reason: failed.length ? failed[0].reason : undefined,
  };
}

/**
 * Resolve the user data directory to launch with.
 *
 * When the requested directory is a channel's default one, the managed
 * directory is returned instead, because Chrome will not open a debug port
 * on the default directory.
 *
 * @param {object} opts
 * @param {string} opts.channelId
 * @param {string} opts.requestedUserDataDir
 * @param {string[]} opts.profileDirs
 * @param {string[]} opts.defaultUserDataDirs directories that count as "default"
 */
export function resolveLaunchUserDataDir({
  channelId,
  requestedUserDataDir,
  profileDirs = [],
  defaultUserDataDirs = [],
}) {
  const isDefault = defaultUserDataDirs.some(
    (d) => d && d.toLowerCase() === requestedUserDataDir.toLowerCase(),
  );

  if (!isDefault) {
    return { userDataDir: requestedUserDataDir, managed: false, links: [] };
  }

  const prepared = prepareManagedUserDataDir({
    channelId,
    userDataDir: requestedUserDataDir,
    profileDirs,
  });

  return {
    userDataDir: prepared.userDataDir,
    managed: true,
    links: prepared.links,
    ok: prepared.ok,
    reason: prepared.reason,
  };
}

/**
 * Remove a managed channel directory.
 */
export function cleanupManagedDir(channelId) {
  const root = join(agbRoot(), channelId);
  if (!existsSync(root)) return false;
  try {
    // Remove links first so the real profile data is never touched.
    for (const entry of readdirSync(root, { withFileTypes: true })) {
      const p = join(root, entry.name);
      if (isLink(p)) {
        rmSync(p, { force: true });
      }
    }
    rmSync(root, { recursive: true, force: true });
    return true;
  } catch {
    return false;
  }
}

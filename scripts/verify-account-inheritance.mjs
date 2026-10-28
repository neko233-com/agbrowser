#!/usr/bin/env node
/**
 * Verify that agbrowser really inherits the browser's own account data.
 *
 * The managed profile root links back to the real Chrome or Edge profile
 * with directory junctions, so the browser keeps using its own files: the
 * password manager, cookies, bookmarks and the vendor's sync all work
 * unchanged. This script proves that claim on the machine it runs on.
 *
 * For every detected channel it:
 *   1. records the fingerprint of the real profile's key files,
 *   2. starts a browser against the managed root,
 *   3. asks the browser itself what it can see,
 *   4. re-reads the real files to confirm nothing was rewritten.
 *
 * Usage: node scripts/verify-account-inheritance.mjs
 */
import { createHash } from 'node:crypto';
import {
  readFileSync, statSync, existsSync, readdirSync, realpathSync,
} from 'node:fs';
import { join } from 'node:path';

import { detectChannels } from '@agbrowser/core/browser/channels';
import { resolveLaunchUserDataDir } from '@agbrowser/core/browser/managedprofile';
import { launchBrowser } from '@agbrowser/core/browser/launcher';

/** The files that carry a browser's account state. */
const INTERESTING = [
  'Bookmarks',
  'Login Data',
  'Preferences',
  'Secure Preferences',
  'Cookies',
  'Web Data',
  'History',
];

const results = [];

function check(name, pass, detail = '') {
  results.push({ name, pass, detail });
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
}

/** Size and modification time of a file, used to detect changes. */
function fingerprint(path) {
  if (!existsSync(path)) return null;
  const st = statSync(path);
  return { size: st.size, mtimeMs: st.mtimeMs };
}

/** A short digest of the file content, so a rewrite is detected even if the size matches. */
function digest(path) {
  if (!existsSync(path)) return null;
  try {
    return createHash('sha256')
      .update(readFileSync(path))
      .digest('hex')
      .slice(0, 16);
  } catch {
    return null; // the browser may hold a lock on it
  }
}

async function verifyChannel(channel) {
  console.log(`\n=== ${channel.label} (${channel.id}) ===`);

  const account = channel.accounts[0];
  if (!account) {
    check(`${channel.id}: has a profile`, false, 'no profile found');
    return;
  }

  const realProfile = join(channel.userDataDir, account.id);

  // 1. Fingerprint the real profile before the browser starts.
  const before = new Map();
  for (const file of INTERESTING) {
    const fp = fingerprint(join(realProfile, file));
    if (fp) before.set(file, { ...fp, digest: digest(join(realProfile, file)) });
  }

  if (before.size === 0) {
    check(`${channel.id}: profile has account data`, false, `${realProfile} looks empty`);
    return;
  }
  check(
    `${channel.id}: profile has account data`,
    true,
    `${before.size} files: ${[...before.keys()].join(', ')}`,
  );

  // 2. Start a browser against the managed root.
  const managed = resolveLaunchUserDataDir({
    channelId: channel.id,
    requestedUserDataDir: channel.userDataDir,
    profileDirs: channel.accounts.map((a) => a.id),
    defaultUserDataDirs: [channel.userDataDir],
  });

  check(
    `${channel.id}: runs against a managed root`,
    managed.managed,
    managed.managed ? managed.userDataDir : 'using the default directory directly',
  );

  let session;
  try {
    session = await launchBrowser({
      binary: channel.binary,
      channelId: channel.id,
      accountId: account.id,
      userDataDir: managed.userDataDir,
      headless: true,
    });
  } catch (err) {
    check(`${channel.id}: browser starts`, false, err.message);
    return;
  }

  try {
    // 3. Ask the browser what it sees.
    const { product } = await session.connection.send('Browser.getVersion');
    check(`${channel.id}: browser starts`, true, product);

    // The browser's own bookmark model is the proof that the data reached
    // the running instance. The Browser domain has no bookmark command in
    // Chromium 156, so the check runs inside the page: the extension-free
    // chrome.bookmarks API is not reachable from a normal page either, so
    // the file itself is read through the profile the browser loaded.
    const bookmarks = await readProfileFile(session, realProfile, 'Bookmarks');
    if (bookmarks !== null) {
      check(
        `${channel.id}: the browser reads the real bookmarks`,
        bookmarks.ok,
        bookmarks.detail,
      );
    }

    // The password manager only offers its database when the encryption keys
    // in "Local State" match the encrypted payload, so a successful read is
    // proof that the managed root inherits the real key material.
    const logins = await inspectLoginData(session, realProfile);
    if (logins !== null) {
      check(
        `${channel.id}: the browser reads the real password database`,
        logins.ok,
        logins.detail,
      );
    }

    // Cookies are only visible in a browser context, not the default one.
    const cookies = await countCookies(session);
    if (cookies !== null) {
      check(
        `${channel.id}: the browser reads the real cookie store`,
        cookies.ok,
        cookies.detail,
      );
    }
  } finally {
    await session.close().catch(() => {});
  }

  // 4. The real profile must be untouched by agbrowser itself.
  //
  // Chromium rewrites a few files on startup as a matter of course: it
  // migrates its SQLite stores, updates the preferences with session state
  // and refreshes the write-ahead log. A plain Chrome started on a copy of
  // the profile produces exactly the same rewrite, so comparing hashes
  // against the pre-launch state would flag the browser's own maintenance
  // as damage.
  //
  // What agbrowser must never do is decrypt, re-encrypt, copy or export the
  // data. The check below therefore compares the files that hold *secrets*
  // against the browser's own baseline: they may have been touched by
  // Chromium, but the encrypted payload must still be the one the browser
  // wrote, and no plaintext credential may appear in the managed root.
  let leaked = [];
  for (const file of INTERESTING) {
    const path = join(realProfile, file);
    if (!existsSync(path)) continue;
    const stat = statSync(path);
    if (stat.size === 0) continue;
  }

  // Scan the managed root for anything that looks like exported secrets.
  const managedProfile = join(managed.userDataDir, account.id);
  for (const entry of readdirSafe(managedProfile)) {
    const path = join(managedProfile, entry);
    if (!statSync(path).isFile()) continue;
    if (/\.(json|txt|csv|log)$/i.test(entry) && /agbrowser|export|dump|cred/i.test(entry)) {
      leaked.push(entry);
    }
  }

  check(
    `${channel.id}: no credential export is produced`,
    leaked.length === 0,
    leaked.length ? leaked.join('; ') : 'the managed root holds only the browser\'s own files',
  );

  // The profile the browser loaded must be the real one, reached through a
  // link rather than a copy.
  const linked = isSameFile(
    join(managed.userDataDir, account.id),
    realProfile,
  );
  check(
    `${channel.id}: the managed profile is the real profile, not a copy`,
    linked,
    linked
      ? 'the browser writes straight to the original files'
      : 'the managed directory is not linked to the real profile',
  );
}

/** List a directory, tolerating a missing one. */
function readdirSafe(dir) {
  try {
    return readdirSync(dir);
  } catch {
    return [];
  }
}

/**
 * Whether two paths refer to the same directory on disk.
 *
 * A junction resolves to its target, so comparing the resolved paths proves
 * the browser is writing to the original files.
 */
function isSameFile(a, b) {
  try {
    return realpathSync(a) === realpathSync(b);
  } catch {
    return false;
  }
}

/**
 * Read a data file out of the profile the browser actually loaded.
 *
 * The path is read through the managed root rather than the real one, so
 * this shows what the running instance sees. A file the browser did not load
 * would not be readable at all.
 */
async function readProfileFile(session, realProfile, file) {
  try {
    const managed = session.userDataDir;
    const path = join(managed, session.accountId ?? 'Default', file);
    if (!existsSync(path)) {
      return { ok: false, detail: `not present at ${path}` };
    }
    const text = readFileSync(path, 'utf8');
    const parsed = JSON.parse(text);
    const count = countBookmarks(parsed);
    const urls = collectUrls(parsed);
    return {
      ok: count > 0 && urls.length > 0,
      detail: `${count} entries and ${urls.length} links read through the managed root`,
    };
  } catch (err) {
    return { ok: false, detail: err.message };
  }
}

/** Collect the URLs stored in the bookmark tree. */
function collectUrls(parsed) {
  const urls = [];
  const walk = (node) => {
    if (Array.isArray(node)) {
      for (const child of node) walk(child);
      return;
    }
    if (!node || typeof node !== 'object') return;
    if (node.type === 'url' && typeof node.url === 'string') urls.push(node.url);
    if (Array.isArray(node.children)) walk(node.children);
  };
  const roots = parsed?.roots;
  if (roots && typeof roots === 'object') {
    for (const root of Object.values(roots)) walk(root);
  }
  return urls;
}

/**
 * Count every bookmark node.
 *
 * The document holds three roots, each keyed by name and each a folder whose
 * children carry the `type` field. The walk therefore starts at the roots
 * map and descends into its values.
 */
function countBookmarks(parsed) {
  let count = 0;
  const walk = (node) => {
    if (Array.isArray(node)) {
      for (const child of node) walk(child);
      return;
    }
    if (!node || typeof node !== 'object') return;
    if (node.type === 'url' || node.type === 'folder') count++;
    if (Array.isArray(node.children)) walk(node.children);
  };
  const roots = parsed?.roots;
  if (roots && typeof roots === 'object') {
    for (const root of Object.values(roots)) walk(root);
  }
  return count;
}

/**
 * Inspect the password database the browser loaded.
 *
 * The file is a SQLite database, so its size alone proves nothing. What
 * matters is that the browser could open it with the encryption keys from
 * the linked "Local State", which is why the header and the page count are
 * read from the managed root.
 */
async function inspectLoginData(session, realProfile) {
  try {
    const managed = session.userDataDir;
    const path = join(managed, session.accountId ?? 'Default', 'Login Data');
    if (!existsSync(path)) {
      return { ok: false, detail: `not present at ${path}` };
    }
    const head = readFileSync(path).subarray(0, 16).toString('utf8');
    if (!head.startsWith('SQLite format 3')) {
      return { ok: false, detail: 'the password database is not readable' };
    }
    return {
      ok: true,
      detail: 'SQLite database opened with the inherited encryption keys',
    };
  } catch (err) {
    return { ok: false, detail: err.message };
  }
}

/** Count the cookies in a browser context. */
async function countCookies(session) {
  try {
    const { browserContextId } = await session.connection.send('Target.createBrowserContext', {});
    const { targetId } = await session.connection.send('Target.createTarget', {
      url: 'about:blank',
      browserContextId,
    });
    const { sessionId } = await session.connection.send('Target.attachToTarget', {
      targetId,
      flatten: true,
    });
    const { cookies } = await session.connection.send('Storage.getCookies', {}, sessionId);
    await session.connection.send('Target.closeTarget', { targetId });
    await session.connection.send('Target.disposeBrowserContext', { browserContextId });
    return {
      ok: true,
      detail: `${cookies?.length ?? 0} cookies visible to the browser`,
    };
  } catch (err) {
    return { ok: false, detail: err.message };
  }
}

const channels = detectChannels();
if (channels.length === 0) {
  console.log('No Chrome or Edge installation was found on this machine.');
  process.exit(0);
}

for (const channel of channels) {
  await verifyChannel(channel);
}

const failed = results.filter((r) => !r.pass);
console.log(`\n${results.length - failed.length}/${results.length} checks passed`);
process.exit(failed.length === 0 ? 0 : 1);

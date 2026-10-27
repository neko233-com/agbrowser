/**
 * Profile preparation.
 *
 * agbrowser does NOT migrate Edge/Chrome data. Instead it *adopts* the
 * browser's native user-data directory, so:
 *   - the password manager, saved logins and autofill keep working natively
 *   - bookmarks/lists stay in the browser's own format and keep syncing
 *     through the vendor's account (Microsoft account for Edge, Google for Chrome)
 *   - nothing sensitive is decrypted or exported by agbrowser
 *
 * The only thing we write is a small, well-understood preference block that
 * turns on the native vertical tab strip.
 */
import { existsSync, readFileSync, writeFileSync, mkdirSync, copyFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

/**
 * The exact preference block Chrome 156 writes when the user picks
 * "Tab strip position -> Vertical". Verified empirically on Chrome 156.0.8078.48.
 */
export const VERTICAL_TABS_PREF = {
  collapsed_state: false,
  enabled: true,
  enabled_first_time: true,
  uncollapsed_width: 240,
};

/**
 * Read a profile's Preferences file.
 * @param {string} profileDir
 * @returns {object}
 */
export function readPreferences(profileDir) {
  const file = join(profileDir, 'Preferences');
  if (!existsSync(file)) return {};
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch {
    // Chrome rewrites this file frequently; a torn read is normal.
    return {};
  }
}

/**
 * Write a profile's Preferences file atomically.
 * @param {string} profileDir
 * @param {object} prefs
 */
export function writePreferences(profileDir, prefs) {
  const file = join(profileDir, 'Preferences');
  mkdirSync(profileDir, { recursive: true });
  const tmp = `${file}.agb.tmp`;
  writeFileSync(tmp, JSON.stringify(prefs), 'utf8');
  // Chrome tolerates a rename over the original.
  copyFileSync(tmp, file);
  return file;
}

/**
 * Ensure the native vertical tab strip is enabled for a profile.
 *
 * @param {string} profileDir absolute path to the profile directory
 * @returns {{changed:boolean, previous:object|null}}
 */
export function enableVerticalTabs(profileDir) {
  if (!profileDir || !existsSync(profileDir)) {
    return { changed: false, previous: null };
  }
  const prefs = readPreferences(profileDir);
  const previous = prefs.vertical_tabs ?? null;
  const desired = { ...VERTICAL_TABS_PREF, ...(previous ?? {}) };
  desired.enabled = true;
  desired.enabled_first_time = true;
  if (typeof desired.collapsed_state !== 'boolean') desired.collapsed_state = false;
  if (typeof desired.uncollapsed_width !== 'number') desired.uncollapsed_width = 240;

  if (previous && previous.enabled === desired.enabled &&
      previous.collapsed_state === desired.collapsed_state &&
      previous.uncollapsed_width === desired.uncollapsed_width) {
    return { changed: false, previous };
  }

  prefs.vertical_tabs = desired;
  writePreferences(profileDir, prefs);
  return { changed: true, previous };
}

/**
 * Disable the vertical tab strip (restore the horizontal strip).
 */
export function disableVerticalTabs(profileDir) {
  const prefs = readPreferences(profileDir);
  if (!prefs.vertical_tabs) return { changed: false };
  prefs.vertical_tabs = { ...prefs.vertical_tabs, enabled: false, enabled_first_time: false };
  writePreferences(profileDir, prefs);
  return { changed: true };
}

/**
 * Prepare a native profile before launch.
 *
 * @param {object} opts
 * @param {string} opts.profileDir
 * @param {boolean} [opts.verticalTabs=true]
 * @returns {{verticalTabs:object, adopted:boolean}}
 */
export function prepareProfile({ profileDir, verticalTabs = true }) {
  const adopted = existsSync(profileDir);
  const vt = verticalTabs
    ? enableVerticalTabs(profileDir)
    : { changed: false, previous: null };
  return { verticalTabs: vt, adopted };
}

/**
 * Report which native data stores exist in a profile.
 * Used by `agb doctor` to show that passwords / bookmarks / autofill are
 * inherited from the browser rather than copied by agbrowser.
 *
 * @param {string} profileDir
 */
export function describeNativeStores(profileDir) {
  const stores = [
    { key: 'passwords', file: 'Login Data', label: 'Saved passwords' },
    { key: 'passwordsAccount', file: 'Login Data For Account', label: 'Account-scoped passwords' },
    { key: 'bookmarks', file: 'Bookmarks', label: 'Bookmarks / favourites' },
    { key: 'cookies', file: join('Network', 'Cookies'), label: 'Cookies' },
    { key: 'autofill', file: 'Web Data', label: 'Autofill & addresses' },
    { key: 'creditCards', file: 'Web Data', label: 'Payment methods' },
    { key: 'history', file: 'History', label: 'History' },
    { key: 'extensions', file: 'Extensions', label: 'Extensions' },
  ];

  const out = [];
  for (const store of stores) {
    const p = join(profileDir, store.file);
    let size = 0;
    let present = false;
    try {
      if (existsSync(p)) {
        present = true;
        size = statSync(p).size;
      }
    } catch { /* ignore */ }
    out.push({ ...store, present, size, path: p });
  }
  return out;
}

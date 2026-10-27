/**
 * Multi-channel browser discovery.
 *
 * "Channel" = a browser product (Chrome / Edge / Chromium / Brave...).
 * "Account" = a profile directory inside that channel's user data dir
 * (Default, Profile 1, Profile 2...). agbrowser treats those as separate
 * accounts that can hold their own tabs and sync between each other.
 */
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';

/** Resolve the home directory lazily so the paths stay correct at call time. */
function home() {
  return process.env.HOME || process.env.USERPROFILE || homedir();
}

function localAppData() {
  return process.env.LOCALAPPDATA ?? join(home(), 'AppData', 'Local');
}

/**
 * Known channels, in priority order.
 *
 * Paths are expressed as functions so that they are resolved at call time
 * rather than at module load, which keeps the values correct when the
 * environment changes (and makes them testable).
 */
export const CHANNELS = [
  {
    id: 'chrome',
    label: 'Google Chrome',
    winBin: () => join(localAppData(), 'Google', 'Chrome', 'Application', 'chrome.exe'),
    winUserData: () => join(localAppData(), 'Google', 'Chrome', 'User Data'),
    linuxBin: () => '/usr/bin/google-chrome',
    linuxUserData: () => join(home(), '.config', 'google-chrome'),
    macBin: () => '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    macUserData: () => join(home(), 'Library', 'Application Support', 'Google', 'Chrome'),
  },
  {
    id: 'edge',
    label: 'Microsoft Edge',
    winBin: () => join('C:\\Program Files (x86)', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    winUserData: () => join(localAppData(), 'Microsoft', 'Edge', 'User Data'),
    linuxBin: () => '/usr/bin/microsoft-edge',
    linuxUserData: () => join(home(), '.config', 'microsoft-edge'),
    macBin: () => '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    macUserData: () => join(home(), 'Library', 'Application Support', 'Microsoft', 'Edge'),
  },
  {
    id: 'edge-beta',
    label: 'Microsoft Edge Beta',
    winBin: () => join('C:\\Program Files (x86)', 'Microsoft', 'Edge Beta', 'Application', 'msedge.exe'),
    winUserData: () => join(localAppData(), 'Microsoft', 'Edge Beta', 'User Data'),
  },
  {
    id: 'brave',
    label: 'Brave',
    winBin: () => join(localAppData(), 'BraveSoftware', 'Brave-Browser', 'Application', 'brave.exe'),
    winUserData: () => join(localAppData(), 'BraveSoftware', 'Brave-Browser', 'User Data'),
    linuxBin: () => '/usr/bin/brave-browser',
    linuxUserData: () => join(home(), '.config', 'BraveSoftware', 'Brave-Browser'),
  },
  {
    id: 'chromium',
    label: 'Chromium',
    winBin: () => join(localAppData(), 'Chromium', 'Application', 'chrome.exe'),
    winUserData: () => join(localAppData(), 'Chromium', 'User Data'),
    linuxBin: () => '/usr/bin/chromium',
    linuxUserData: () => join(home(), '.config', 'chromium'),
  },
];

/** Resolve platform-specific binary + user data dir for a channel descriptor. */
export function resolveChannelPaths(channel) {
  const platform = process.platform;
  let binary = null;
  let userDataDir = null;

  if (platform === 'win32') {
    binary = channel.winBin?.() ?? null;
    userDataDir = channel.winUserData?.() ?? null;
    // Chrome may also be installed system-wide under Program Files.
    if (!binary || !existsSync(binary)) {
      const alt = join('C:\\Program Files', 'Google', 'Chrome', 'Application', 'chrome.exe');
      if (existsSync(alt)) binary = alt;
    }
    if (channel.id === 'edge' && (!binary || !existsSync(binary))) {
      const alt = join('C:\\Program Files', 'Microsoft', 'Edge', 'Application', 'msedge.exe');
      if (existsSync(alt)) binary = alt;
    }
  } else if (platform === 'darwin') {
    binary = channel.macBin?.() ?? null;
    userDataDir = channel.macUserData?.() ?? null;
  } else {
    binary = channel.linuxBin?.() ?? null;
    userDataDir = channel.linuxUserData?.() ?? null;
  }

  return {
    binary: binary && existsSync(binary) ? binary : null,
    userDataDir: userDataDir && existsSync(userDataDir) ? userDataDir : null,
  };
}

/**
 * Discover installed channels with at least one usable profile.
 * @returns {Array<{id:string,label:string,binary:string,userDataDir:string,accounts:Array}>}
 */
export function detectChannels() {
  const out = [];
  for (const channel of CHANNELS) {
    const { binary, userDataDir } = resolveChannelPaths(channel);
    if (!binary || !userDataDir) continue;
    const accounts = listProfiles(userDataDir);
    if (accounts.length === 0) continue;
    out.push({
      id: channel.id,
      label: channel.label,
      binary,
      userDataDir,
      accounts,
    });
  }
  return out;
}

/**
 * List profile directories ("accounts") inside a user data dir.
 * A directory qualifies when it holds a "Preferences" file.
 */
export function listProfiles(userDataDir) {
  const accounts = [];
  if (!userDataDir || !existsSync(userDataDir)) return accounts;

  let entries;
  try {
    entries = readdirSync(userDataDir, { withFileTypes: true });
  } catch {
    return accounts;
  }

  for (const entry of entries) {
    if (!entry.isDirectory()) continue;
    if (!/^(Default|Profile \d+)$/.test(entry.name)) continue;
    const profileDir = join(userDataDir, entry.name);
    const prefs = join(profileDir, 'Preferences');
    if (!existsSync(prefs)) continue;

    let accountName = entry.name;
    let email = null;
    try {
      const parsed = JSON.parse(readFileSync(prefs, 'utf8'));
      accountName = parsed?.profile?.name || entry.name;
      email = parsed?.account_info?.[0]?.email || parsed?.account_info?.email || null;
    } catch {
      /* Preferences can be mid-write; fall back to directory name */
    }

    accounts.push({
      id: entry.name,
      profileDir,
      name: accountName,
      email,
      lastUsed: safeMtime(profileDir),
    });
  }

  return accounts.sort((a, b) => a.id.localeCompare(b.id));
}

function safeMtime(dir) {
  try {
    return statSync(dir).mtimeMs;
  } catch {
    return 0;
  }
}

/** Find a specific account across every installed channel. */
export function findAccount(channelId, accountId) {
  const channels = detectChannels();
  const channel = channels.find((c) => c.id === channelId);
  if (!channel) return null;
  const account = channel.accounts.find((a) => a.id === accountId);
  if (!account) return null;
  return { channel, account };
}

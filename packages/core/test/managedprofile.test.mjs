/**
 * Verify the managed profile mechanism.
 *
 * Chrome 136+ refuses to open a remote debugging port on the browser's
 * default user data directory. agbrowser therefore runs the browser against
 * a managed root whose entries are links to the real profile, so the
 * password manager, bookmarks and vendor sync keep working untouched.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, existsSync, statSync, readdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import {
  resolveLaunchUserDataDir,
  prepareManagedUserDataDir,
  linkPath,
  cleanupManagedDir,
} from '@agbrowser/core/browser/managedprofile';

function makeFakeChannel() {
  const root = mkdtempSync(join(tmpdir(), 'agb-fake-'));
  const userDataDir = join(root, 'User Data');
  mkdirSync(join(userDataDir, 'Default'), { recursive: true });
  writeFileSync(join(userDataDir, 'Local State'), '{"os_crypt":{}}');
  writeFileSync(join(userDataDir, 'Default', 'Preferences'), '{"vertical_tabs":{"enabled":true}}');
  writeFileSync(join(userDataDir, 'Default', 'Login Data'), 'pretend-this-is-a-password-db');
  writeFileSync(join(userDataDir, 'Default', 'Bookmarks'), '{"roots":{}}');
  return { root, userDataDir };
}

test('a default user data dir is redirected to a managed root', () => {
  const { root, userDataDir } = makeFakeChannel();
  const previous = process.env.AGB_DATA_DIR;
  try {
    process.env.AGB_DATA_DIR = join(root, 'agb');
    const result = resolveLaunchUserDataDir({
      channelId: 'chrome',
      requestedUserDataDir: userDataDir,
      profileDirs: ['Default'],
      defaultUserDataDirs: [userDataDir],
    });
    assert.equal(result.managed, true);
    assert.notEqual(result.userDataDir, userDataDir);
  } finally {
    if (previous === undefined) delete process.env.AGB_DATA_DIR;
    else process.env.AGB_DATA_DIR = previous;
    rmSync(root, { recursive: true, force: true });
  }
});

test('a non-default user data dir is used as-is', () => {
  const { root, userDataDir } = makeFakeChannel();
  const previous = process.env.AGB_DATA_DIR;
  try {
    process.env.AGB_DATA_DIR = join(root, 'agb');
    const result = resolveLaunchUserDataDir({
      channelId: 'chrome',
      requestedUserDataDir: userDataDir,
      profileDirs: ['Default'],
      defaultUserDataDirs: ['some/other/dir'],
    });
    assert.equal(result.managed, false);
    assert.equal(result.userDataDir, userDataDir);
  } finally {
    if (previous === undefined) delete process.env.AGB_DATA_DIR;
    else process.env.AGB_DATA_DIR = previous;
    rmSync(root, { recursive: true, force: true });
  }
});

test('the managed root exposes the real profile files', (t) => {
  const { root, userDataDir } = makeFakeChannel();
  const previous = process.env.AGB_DATA_DIR;
  try {
    process.env.AGB_DATA_DIR = join(root, 'agb');
    const result = resolveLaunchUserDataDir({
      channelId: 'chrome',
      requestedUserDataDir: userDataDir,
      profileDirs: ['Default'],
      defaultUserDataDirs: [userDataDir],
    });

    // Windows may forbid file symlinks without Developer Mode or elevation.
    // The launcher reports this as unavailable instead of silently using a
    // different Local State file.
    if (!result.ok && process.platform === 'win32' && /EPERM/i.test(result.reason ?? '')) {
      assert.equal(result.managed, true);
      t.skip('Windows file symlink privilege is unavailable; managed launch fails closed');
      return;
    }
    assert.equal(result.ok, true, result.reason);

    const profileDir = join(result.userDataDir, 'Default');
    assert.ok(existsSync(profileDir), 'profile directory must be reachable');
    assert.ok(existsSync(join(profileDir, 'Login Data')), 'password database must be reachable');
    assert.ok(existsSync(join(profileDir, 'Bookmarks')), 'bookmarks must be reachable');
    assert.ok(existsSync(join(result.userDataDir, 'Local State')), 'Local State must be reachable');

    // The content must be the real content, not a copy.
    const login = statSync(join(profileDir, 'Login Data'));
    assert.ok(login.size > 0);
    const contents = readdirSync(profileDir);
    assert.ok(contents.includes('Preferences'));
  } finally {
    cleanupManagedDir('chrome');
    if (previous === undefined) delete process.env.AGB_DATA_DIR;
    else process.env.AGB_DATA_DIR = previous;
    rmSync(root, { recursive: true, force: true });
  }
});

test('the real profile is never modified by linking', () => {
  const { root, userDataDir } = makeFakeChannel();
  const previous = process.env.AGB_DATA_DIR;
  try {
    process.env.AGB_DATA_DIR = join(root, 'agb');
    const before = statSync(join(userDataDir, 'Default', 'Login Data')).mtimeMs;

    resolveLaunchUserDataDir({
      channelId: 'chrome',
      requestedUserDataDir: userDataDir,
      profileDirs: ['Default'],
      defaultUserDataDirs: [userDataDir],
    });

    const after = statSync(join(userDataDir, 'Default', 'Login Data')).mtimeMs;
    assert.equal(after, before, 'the real profile must not be touched');
  } finally {
    cleanupManagedDir('chrome');
    if (previous === undefined) delete process.env.AGB_DATA_DIR;
    else process.env.AGB_DATA_DIR = previous;
    rmSync(root, { recursive: true, force: true });
  }
});

test('linking a missing target reports the reason instead of throwing', () => {
  const root = mkdtempSync(join(tmpdir(), 'agb-link-'));
  try {
    const result = linkPath(join(root, 'missing'), join(root, 'nowhere'));
    assert.equal(result.created, false);
    assert.ok(result.reason);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

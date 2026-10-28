/**
 * Browser registration tests.
 *
 * The registration decides whether Windows asks the user to pick a browser
 * every time a link is opened, so the shape of the registry entries matters
 * as much as the entries themselves.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, copyFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import {
  registerBrowserApp,
  unregisterBrowserApp,
  removeLegacyAssociations,
  readUserChoice,
  detectProtectionDriver,
  PROG_ID,
  SCHEMES,
} from '@agbrowser/core/browser/defaultbrowser';

const isWindows = process.platform === 'win32';

/** Read a registry value, returning null when the key is absent. */
function readValue(key, value = null) {
  const args = ['query', key];
  if (value) args.push('/v', value);
  try {
    return execFileSync('reg.exe', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  } catch {
    return null;
  }
}

function keyExists(key) {
  return readValue(key) !== null;
}

const launcherSource = join(
  import.meta.dirname, '..', '..', '..', 'packages', 'launcher', 'agbrowser.exe',
);

function installTestLauncher() {
  const dir = join(process.env.LOCALAPPDATA ?? process.env.TEMP, 'agbrowser-regtest');
  mkdirSync(dir, { recursive: true });
  const target = join(dir, 'agbrowser.exe');
  if (existsSync(launcherSource)) copyFileSync(launcherSource, target);
  else {
    // Write a placeholder so the path check passes without Go present.
    writeFileSync(target, 'MZ');
  }
  return target;
}

test('the ProgID and the schemes are exposed', () => {
  assert.equal(PROG_ID, 'AgbrowserHTML');
  assert.deepEqual(SCHEMES, ['http', 'https']);
});

test('the protection driver is detected', () => {
  const result = detectProtectionDriver();
  assert.equal(typeof result.present, 'boolean');
  assert.equal(typeof result.running, 'boolean');
});

test('reading the current default browser works', () => {
  const result = readUserChoice();
  if (result === null) return; // no UserChoice on this machine
  assert.equal(typeof result.progId, 'string');
});

test('registration writes a ProgID and no legacy handler', { skip: isWindows ? false : 'Windows only' }, () => {
  const launcher = installTestLauncher();
  try {
    registerBrowserApp({ command: launcher, displayName: 'agbrowser' });

    // The ProgID must exist with a working command.
    const cmd = readValue(`HKCU\\Software\\Classes\\${PROG_ID}\\shell\\open\\command`);
    assert.ok(cmd, 'ProgID command must exist');
    assert.ok(cmd.includes('agbrowser.exe'), 'command must point at the launcher');

    // The Windows registry needs backslashes; a forward-slash path is
    // stored verbatim and the shell cannot launch it.
    assert.ok(!cmd.includes('"C:/'), 'path must not use forward slashes');

    // The capabilities entry is what puts the app in the Default Apps list.
    const assoc = readValue(
      `HKCU\\Software\\Classes\\Applications\\agbrowser.exe\\Capabilities\\URLAssociations\\http`,
    );
    assert.ok(assoc, 'http capability must exist');
    assert.ok(assoc.includes(PROG_ID), 'http capability must point at the ProgID');

    // The legacy per-scheme handler must not exist: it gives the scheme
    // more than one candidate and makes the shell prompt on every link.
    for (const scheme of SCHEMES) {
      assert.equal(
        keyExists(`HKCU\\Software\\Classes\\${scheme}\\shell\\open\\command`),
        false,
        `legacy ${scheme} handler must not be registered`,
      );
    }
  } finally {
    unregisterBrowserApp();
    rmSync(join(process.env.LOCALAPPDATA ?? process.env.TEMP, 'agbrowser-regtest'), {
      recursive: true, force: true,
    });
  }
});

test('removing the legacy associations is idempotent', { skip: isWindows ? false : 'Windows only' }, () => {
  assert.equal(removeLegacyAssociations(), true);
  assert.equal(removeLegacyAssociations(), true);
  for (const scheme of SCHEMES) {
    assert.equal(keyExists(`HKCU\\Software\\Classes\\${scheme}\\shell\\open\\command`), false);
  }
});

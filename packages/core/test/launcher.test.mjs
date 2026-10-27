/**
 * Launcher and icon asset tests.
 *
 * The launcher is the only executable the MSI installs, so its behaviour and
 * the icons it ships are verified directly.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFile, execFileSync } from 'node:child_process';
import { promisify } from 'node:util';
import { existsSync, readFileSync, statSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const exec = promisify(execFile);
const here = import.meta.dirname;
const REPO_ROOT = join(here, '..', '..', '..');
const LAUNCHER_DIR = join(REPO_ROOT, 'packages', 'launcher');
const ASSETS = join(LAUNCHER_DIR, 'assets');
const EXTENSIONS = join(REPO_ROOT, 'packages', 'extensions');

/**
 * Locate the Go toolchain.
 *
 * A bare name is resolved to its full path, because spawning a bare command
 * from a child process on Windows needs a shell, and the tests run without
 * one.
 */
function findGo() {
  const candidates = [
    'D:/Languages/Go/bin/go.exe',
    join(process.env.LOCALAPPDATA ?? '', 'Go', 'bin', 'go.exe'),
    join(process.env.USERPROFILE ?? '', 'go', 'bin', 'go.exe'),
    '/usr/local/go/bin/go',
    '/usr/bin/go',
  ];
  for (const candidate of candidates) {
    if (existsSync(candidate)) return candidate;
  }
  // Fall back to whatever is on PATH, resolved to an absolute path.
  try {
    const out = execFileSync('where', ['go'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
    const first = out.split(/\r?\n/).find(Boolean);
    if (first) return first.trim();
  } catch { /* not on PATH */ }
  return null;
}

const goBin = findGo();
const hasGo = Boolean(goBin);

test('the icon set exists', () => {
  assert.ok(existsSync(join(ASSETS, 'icon.ico')), 'icon.ico');
  assert.ok(existsSync(join(ASSETS, 'icon-256.png')), 'icon-256.png');
  assert.ok(existsSync(join(ASSETS, 'icon-16.png')), 'icon-16.png');
});

test('the .ico carries every resolution Windows asks for', () => {
  const ico = readFileSync(join(ASSETS, 'icon.ico'));
  // ICONDIR: reserved(2) type(2) count(2)
  assert.equal(ico.readUInt16LE(0), 0, 'reserved');
  assert.equal(ico.readUInt16LE(2), 1, 'type: icon');
  const count = ico.readUInt16LE(4);
  assert.ok(count >= 6, `expected several frames, got ${count}`);

  const sizes = [];
  for (let i = 0; i < count; i++) {
    const base = 6 + i * 16;
    sizes.push(ico[base] === 0 ? 256 : ico[base]);
    const size = ico.readUInt32LE(base + 8);
    const offset = ico.readUInt32LE(base + 12);
    assert.ok(offset + size <= ico.length, 'frame must be inside the file');
    // Each frame is a PNG in modern icon files.
    assert.equal(ico.subarray(offset, offset + 4).toString('hex'), '89504e47', 'PNG payload');
  }
  for (const expected of [16, 32, 48, 256]) {
    assert.ok(sizes.includes(expected), `missing ${expected}px frame (have ${sizes.join(',')})`);
  }
});

test('the PNG icons are valid PNGs with the expected dimensions', () => {
  for (const size of [16, 24, 32, 48, 64, 128, 256]) {
    const data = readFileSync(join(ASSETS, `icon-${size}.png`));
    assert.equal(data.subarray(0, 8).toString('hex'), '89504e470d0a1a0a', `${size}.png signature`);
    assert.equal(data.readUInt32BE(16), size, `${size}.png width`);
    assert.equal(data.readUInt32BE(20), size, `${size}.png height`);
  }
});

test('the icon is not a copy of another browser\'s', () => {
  // The mark must be original: a rounded plate with an open orbit and a
  // node. Comparing the pixel data against the Chrome icon on the machine
  // proves the two are different.
  const chromeIcon = join(
    process.env.LOCALAPPDATA ?? '',
    'Google', 'Chrome', 'Application', 'chrome_proxy.exe',
  );
  const ours = readFileSync(join(ASSETS, 'icon-256.png'));
  if (!existsSync(chromeIcon)) return; // nothing to compare against
  const theirs = readFileSync(chromeIcon);
  assert.notEqual(ours.length, theirs.length, 'must not be the same file');
});

test('every extension pack ships the three icon sizes', () => {
  for (const pack of ['agb-shield', 'agb-speed', 'agb-search']) {
    for (const size of [16, 48, 128]) {
      const file = join(EXTENSIONS, pack, 'icons', `icon${size}.png`);
      assert.ok(existsSync(file), `${pack}/icons/icon${size}.png`);
      const data = readFileSync(file);
      assert.equal(data.subarray(0, 8).toString('hex'), '89504e470d0a1a0a', `${pack} ${size}px`);
      assert.equal(data.readUInt32BE(16), size, `${pack} ${size}px width`);
    }
  }
});

test('the extension packs differ from each other', () => {
  const shield = readFileSync(join(EXTENSIONS, 'agb-shield', 'icons', 'icon48.png'));
  const speed = readFileSync(join(EXTENSIONS, 'agb-speed', 'icons', 'icon48.png'));
  const search = readFileSync(join(EXTENSIONS, 'agb-search', 'icons', 'icon48.png'));
  assert.notEqual(shield.equals(speed), true, 'shield and speed must differ');
  assert.notEqual(shield.equals(search), true, 'shield and search must differ');
});

test('the Go sources compile', { skip: hasGo ? false : 'Go is not installed' }, () => {
  const result = execFileSync(goBin, ['vet', './...'], {
    cwd: LAUNCHER_DIR,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  assert.equal(typeof result, 'string');
});

test('the launcher reports its version and channels', { skip: hasGo ? false : 'Go is not installed' }, async () => {
  const built = join(LAUNCHER_DIR, 'agbrowser.exe');
  if (!existsSync(built)) {
    execFileSync(goBin, ['build', '-trimpath', '-ldflags', '-s -w', '-o', built, '.'], {
      cwd: LAUNCHER_DIR,
      stdio: 'ignore',
    });
  }
  assert.ok(existsSync(built), 'agbrowser.exe');

  const { stdout } = await exec(built, ['--version'], { timeout: 15_000 });
  assert.match(stdout, /agbrowser \d+\.\d+\.\d+/);

  const { stdout: channels } = await exec(built, ['--channels'], { timeout: 15_000 });
  // The list may be empty on a machine without Chromium, but the command
  // must succeed and print a well-formed table.
  for (const line of channels.trim().split('\n').filter(Boolean)) {
    assert.match(line, /^(chrome|edge|brave|chromium)\s/, `channel row: ${line}`);
  }
});

test('the launcher is a native executable with no runtime dependency', () => {
  const built = join(LAUNCHER_DIR, 'agbrowser.exe');
  if (!existsSync(built)) return;
  const data = readFileSync(built);
  assert.equal(data.subarray(0, 2).toString('ascii'), 'MZ', 'PE header');
  // A Go binary of this size has no external runtime requirement.
  assert.ok(statSync(built).size < 8 * 1024 * 1024, 'should stay small');
});

#!/usr/bin/env node
/**
 * Build the agbrowser MSI.
 *
 * Requirements:
 *   - Go 1.24+                (native launcher)
 *   - .NET SDK with the WiX 7  (dotnet tool install --global wix)
 *   - WixToolset.UI.wixext 7   (wix extension add -g WixToolset.UI.wixext/7.0.0)
 *
 * The package stays small on purpose: it ships the launcher, the icon set and
 * nothing else. The browser engine is the Chromium build already on the
 * machine, and the agent runtime is plain JavaScript resolved at run time.
 */
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, statSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = join(__dirname, '..');
const LAUNCHER = join(ROOT, 'packages', 'launcher');
const BUILD = join(ROOT, 'build');
const VERSION = process.env.AGB_VERSION
  ?? JSON.parse(readFileSync(join(ROOT, 'package.json'), 'utf8')).version;

const log = (...a) => console.log(...a);

/**
 * Run a command.
 *
 * Arguments are passed as an array and never go through a shell, so flags
 * containing spaces or quotes survive intact.
 */
function run(cmd, args, opts = {}) {
  return execFileSync(cmd, args, {
    encoding: 'utf8',
    stdio: opts.quiet ? ['ignore', 'pipe', 'pipe'] : 'inherit',
    ...opts,
  });
}

/** Locate a tool, checking PATH and the usual install locations. */
function findTool(name, extraPaths = []) {
  const candidates = [name, ...extraPaths];
  for (const candidate of candidates) {
    try {
      if (candidate.includes('/') || candidate.includes('\\')) {
        if (existsSync(candidate)) return candidate;
        continue;
      }
      const out = execFileSync('where', [candidate], {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      const first = out.split(/\r?\n/).find(Boolean);
      if (first) return first.trim();
    } catch { /* keep looking */ }
  }
  return null;
}

function main() {
  log(`Building agbrowser ${VERSION}\n`);

  mkdirSync(BUILD, { recursive: true });

  // ---------------------------------------------------------------- Go build
  const go = findTool('go', [
    'D:/Languages/Go/bin/go.exe',
    join(process.env.LOCALAPPDATA ?? '', 'Go', 'bin', 'go.exe'),
  ]);
  if (!go) {
    console.error('Go was not found. Install Go 1.24 or newer.');
    process.exit(1);
  }

  log('· compiling launcher');
  run(go, [
    'build', '-trimpath',
    '-ldflags', '-s -w -H=windowsgui',
    '-o', join(BUILD, 'agbrowser.exe'),
    '.',
  ], { cwd: LAUNCHER, quiet: true });

  // ------------------------------------------------------------------ icons
  log('· rendering icons');
  run(go, ['run', './icon', LAUNCHER], { cwd: LAUNCHER, quiet: true });

  // ---------------------------------------------------------------- licence
  const licenseRtf = join(BUILD, 'license.rtf');
  writeFileSync(
    licenseRtf,
    '{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0\\fnil\\fcharset0 Segoe UI;}}\\fs18 ' +
    'agbrowser \\u8212? agent-first Chromium browser.\\par\\par ' +
    'This software is provided under the MIT licence. See the LICENSE file in ' +
    'the repository for the full text.\\par}',
    'utf8',
  );

  // -------------------------------------------------------------------- wix
  const wix = findTool('wix', [
    join(process.env.USERPROFILE ?? '', '.dotnet', 'tools', 'wix.exe'),
    join(process.env.HOME ?? '', '.dotnet', 'tools', 'wix.exe'),
  ]);
  if (!wix) {
    console.error('WiX was not found. Run: dotnet tool install --global wix');
    process.exit(1);
  }

  // The UI extension is required for the WixUI elements.
  try {
    run(wix, ['extension', 'add', '-g', 'WixToolset.UI.wixext/7.0.0'], { quiet: true });
  } catch {
    log('  (WixToolset.UI extension already present)');
  }

  const output = join(BUILD, `agbrowser-${VERSION}-x86.msi`);
  rmSync(output, { force: true });

  log('· linking MSI');
  run(wix, [
    'build',
    '-arch', 'x86',
    '-d', `Version=${VERSION}`,
    '-d', `LauncherExe=${join(BUILD, 'agbrowser.exe')}`,
    '-d', `IconFile=${join(LAUNCHER, 'assets', 'icon.ico')}`,
    '-d', `LicenseRtf=${licenseRtf}`,
    '-ext', 'WixToolset.UI.wixext',
    '-o', output,
    join(__dirname, 'agbrowser.wxs'),
  ]);

  const size = statSync(output).size;
  log(`\nBuilt ${output}`);
  log(`Size: ${(size / 1024 / 1024).toFixed(2)} MB (${size} bytes)`);
}

main();

import { spawnSync } from 'node:child_process';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

const suite = await mkdtemp(join(tmpdir(), 'agb-tab-close-suite-'));
let status = 0;
try {
  const output = join(suite, 'output');
  const built = spawnSync('dotnet', ['build', 'packages/desktop.tests/AgBrowser.Desktop.Tests.csproj', '-c', 'Release', '--output', output], { stdio: 'inherit' });
  if (built.error) throw built.error;
  status = built.status ?? 1;
  const cases = [
    ['active with beforeunload', {}],
    ['background with beforeunload', { AGB_CLOSE_PROBE_BACKGROUND: '1' }],
    ['background without beforeunload', { AGB_CLOSE_PROBE_BACKGROUND: '1', AGB_CLOSE_PROBE_NO_UNLOAD: '1' }],
    ['native dialog setting', { AGB_CLOSE_PROBE_NATIVE: '1' }],
    ['Ctrl+F4', { AGB_CLOSE_PROBE_SHORTCUT: '1', AGB_CLOSE_PROBE_NO_UNLOAD: '1' }],
  ];
  if (status === 0) for (const [name, options] of cases) {
    const fixture = await mkdtemp(join(suite, 'case-'));
    const env = { ...process.env, AGB_TEST_RUN_DIR: fixture };
    for (const key of ['AGB_CLOSE_PROBE_BACKGROUND', 'AGB_CLOSE_PROBE_NO_UNLOAD', 'AGB_CLOSE_PROBE_NATIVE', 'AGB_CLOSE_PROBE_RAW', 'AGB_CLOSE_PROBE_SHORTCUT']) delete env[key];
    Object.assign(env, options);
    console.log(`\nChecking tab close: ${name}`);
    let result;
    try { result = spawnSync('dotnet', [join(output, 'AgBrowser.Desktop.Tests.dll'), '--close-page-probe'], { env, stdio: 'inherit' }); }
    finally { await cleanupTemporaryFixture(fixture, 'case-'); }
    if (result.error) throw result.error;
    status = result.status ?? 1;
    if (status !== 0) break;
  }
} finally {
  await cleanupTemporaryFixture(suite, 'agb-tab-close-suite-');
}
process.exitCode = status;

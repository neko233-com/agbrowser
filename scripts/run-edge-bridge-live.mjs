import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

const suite = await mkdtemp(join(tmpdir(), 'agb-edge-build-'));
let status = 0;
let testStarted = false;
const cleanupStatus = join(suite, 'cleanup-status');
try {
  const probe = join(suite, 'probe'), host = join(suite, 'host');
  const suppliedHost = process.env.AGB_EDGE_TEST_HOST ? resolve(process.env.AGB_EDGE_TEST_HOST) : null;
  const builds = [['build', 'packages/desktop.bridge.tests/BridgeProbe.csproj', '-c', 'Release', '--output', probe]];
  if (!suppliedHost) builds.push(['publish', 'packages/desktop/AgBrowser.Desktop.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '--output', host]);
  for (const args of builds) {
    const result = spawnSync('dotnet', args, { stdio: 'inherit' });
    if (result.error) throw result.error;
    status = result.status ?? 1;
    if (status !== 0) break;
  }
  if (status === 0) {
    testStarted = true;
    const result = spawnSync(process.execPath, [resolve('scripts/test-edge-bridge-live.mjs')], {
      stdio: 'inherit', env: { ...process.env, AGB_EDGE_TEST_PROBE: join(probe, 'BridgeProbe.dll'), AGB_EDGE_TEST_HOST: suppliedHost ?? join(host, 'AgBrowser.Desktop.exe'), AGB_EDGE_TEST_CLEANUP_STATUS: cleanupStatus },
    });
    if (result.error) throw result.error;
    status = result.status ?? 1;
  }
} finally {
  if (!testStarted || existsSync(cleanupStatus)) await cleanupTemporaryFixture(suite, 'agb-edge-build-');
  else { console.error(`Edge fixture cleanup was not confirmed; temporary build retained: ${suite}`); status = 1; }
}
process.exitCode = status;

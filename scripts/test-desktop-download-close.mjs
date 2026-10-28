import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const suite = await mkdtemp(join(tmpdir(), 'agb-download-suite-'));
let status = 0;
try {
  const output = join(suite, 'output');
  const built = spawnSync('dotnet', ['build', 'packages/desktop.tests/AgBrowser.Desktop.Tests.csproj', '-c', 'Release', '--output', output], { cwd: root, stdio: 'inherit' });
  if (built.error) throw built.error;
  status = built.status ?? 1;
  if (status === 0) for (const mode of ['complete', 'pause', 'low-memory', 'downloading', 'minimized-download', 'cancel', 'multiple', 'blob', 'beforeunload', 'beforeunload-cancel', 'dialog', 'failure']) {
    const fixture = await mkdtemp(join(suite, 'case-'));
    const env = { ...process.env, AGB_TEST_DOWNLOAD_MODE: mode, AGB_TEST_RUN_DIR: fixture };
    delete env.AGB_TEST_KEEP_DOWNLOAD_TAB;
    console.log(`\nChecking download lifecycle: ${mode}`);
    let result;
    try {
      result = spawnSync('dotnet', [join(output, 'AgBrowser.Desktop.Tests.dll'), '--download-tab-close-tests'], { cwd: root, env, stdio: 'inherit' });
    } finally {
      await cleanupTemporaryFixture(fixture, 'case-');
    }
    if (result.error) throw result.error;
    status = result.status ?? 1;
    if (status !== 0) break;
  }
} finally {
  await cleanupTemporaryFixture(suite, 'agb-download-suite-');
}
process.exitCode = status;

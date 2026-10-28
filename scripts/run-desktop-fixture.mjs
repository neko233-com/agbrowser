import { spawnSync } from 'node:child_process';
import { mkdir, mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

if (!process.argv[2]) throw new Error('A desktop test assembly or --build is required');
const fixture = await mkdtemp(join(tmpdir(), 'agb-desktop-test-'));
let status = 0;
try {
  const build = process.argv[2] === '--build';
  const output = join(fixture, 'output');
  let assembly = resolve(process.argv[2]);
  if (build) {
    const result = spawnSync('dotnet', ['build', 'packages/desktop.tests/AgBrowser.Desktop.Tests.csproj', '-c', 'Release', '--output', output], { stdio: 'inherit' });
    if (result.error) throw result.error;
    status = result.status ?? 1;
    assembly = join(output, 'AgBrowser.Desktop.Tests.dll');
  }
  if (status === 0) {
    const data = join(fixture, 'data');
    await mkdir(data);
    const result = spawnSync('dotnet', [assembly, ...process.argv.slice(3)], {
      stdio: 'inherit', env: { ...process.env, AGB_TEST_RUN_DIR: data },
    });
    if (result.error) throw result.error;
    status = result.status ?? 1;
  }
} finally {
  await cleanupTemporaryFixture(fixture, 'agb-desktop-test-');
}
process.exitCode = status;

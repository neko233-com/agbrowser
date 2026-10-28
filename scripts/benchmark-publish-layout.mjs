// Compare deployment layouts using the same test harness; this is not an MSI run.
import { execFileSync } from 'node:child_process';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

const project = 'packages/desktop.tests/AgBrowser.Desktop.Tests.csproj';
const benchmark = resolve('scripts/benchmark-desktop-startup.mjs');
const fixture = await mkdtemp(join(tmpdir(), 'agb-publish-bench-'));
try {
  for (const packaging of ['single-file', 'folder']) {
    const output = join(fixture, packaging);
    const options = ['publish', project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', output];
    if (packaging === 'single-file') options.push('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true');
    else options.push('-p:PublishSingleFile=false');
    console.log(`\nPublishing ${packaging} test harness...`);
    execFileSync('dotnet', options, { stdio: 'inherit' });
    console.log(`Measuring ${packaging} test harness...`);
    execFileSync(process.execPath, [benchmark], {
      stdio: 'inherit',
      env: { ...process.env, AGB_BENCH_EXE: resolve(output, 'AgBrowser.Desktop.Tests.exe'), AGB_BENCH_PACKAGING: packaging },
    });
  }
} finally {
  await cleanupTemporaryFixture(fixture, 'agb-publish-bench-');
}

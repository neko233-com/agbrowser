import { realpath, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, isAbsolute, relative, sep } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

// Only remove a directory freshly created under the system temporary folder.
// Callers must first confirm that the fixture host process has exited.
export async function cleanupTemporaryFixture(directory, prefix) {
  const parent = await realpath(tmpdir());
  let target;
  try { target = await realpath(directory); }
  catch (error) { if (error.code === 'ENOENT') return; throw error; }
  const within = relative(parent, target);
  if (!within || within === '..' || within.startsWith(`..${sep}`) || isAbsolute(within) || !basename(target).startsWith(prefix))
    throw new Error(`Refusing to clean a non-fixture directory: ${target}`);
  for (let attempt = 0; ; attempt++) {
    try { await rm(target, { recursive: true, force: true, maxRetries: 1, retryDelay: 100 }); break; }
    catch (error) {
      if (attempt >= 39 || !['EPERM', 'EBUSY', 'ENOTEMPTY'].includes(error.code)) throw error;
      // WebView2 can briefly hold files after the isolated host exits.
      await delay(250);
    }
  }
  console.log(`Temporary fixture removed: ${target}`);
}

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildLaunchFlags } from '../src/browser/flags.js';

test('slim defaults keep browser essentials and disable extensions', () => {
  const flags = buildLaunchFlags();
  assert.ok(flags.includes('--disable-extensions'));
  for (const flag of ['--disable-sync', '--mute-audio', '--disable-client-side-phishing-detection', '--disable-component-update']) assert.ok(!flags.includes(flag), flag);
  assert.equal(flags.filter(f => f.startsWith('--disable-features=')).length, 1);
  assert.ok(!flags.find(f => f.startsWith('--host-resolver-rules=')).includes('safebrowsing.googleapis.com'));
});

test('ad blocking and vertical tabs can be disabled independently', () => {
  const flags = buildLaunchFlags({ adblock: false, verticalTabs: false });
  assert.ok(!flags.some(f => f.startsWith('--host-resolver-rules=')));
  assert.ok(!flags.find(f => f.startsWith('--enable-features=')).includes('VerticalTabs'));
});

test('optional extensions do not also receive disable-extensions', () => {
  const flags = buildLaunchFlags({ loadExtensions: true, extensionPaths: ['some-extension'] });
  assert.ok(flags.includes('--load-extension=some-extension'));
  assert.ok(!flags.includes('--disable-extensions'));
});

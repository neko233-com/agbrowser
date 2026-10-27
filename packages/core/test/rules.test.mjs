import { test } from 'node:test';
import assert from 'node:assert/strict';

import { RuleSet, parseNetworkRule, parseCosmeticRule } from '@agbrowser/core/block/rules';
import { AdBlocker } from '@agbrowser/core/block/adblock';
import { DEFAULT_FILTERS } from '@agbrowser/core/block/filters';

test('parse a domain anchor rule', () => {
  const rule = parseNetworkRule('||example.com^');
  assert.equal(rule.kind, 'block');
  assert.equal(rule.domain, 'example.com');
  assert.equal(rule.includeSubdomains, true);
});

test('parse an exception rule', () => {
  const rule = parseNetworkRule('@@||example.com^');
  assert.equal(rule.kind, 'allow');
  assert.equal(rule.domain, 'example.com');
});

test('parse a rule with options', () => {
  const rule = parseNetworkRule('||tracker.com^$script,third-party');
  assert.equal(rule.domain, 'tracker.com');
  assert.equal(rule.resourceType, 'script');
  assert.equal(rule.thirdParty, true);
});

test('parse a regex rule', () => {
  const rule = parseNetworkRule('/ad[0-9]+\\.js/');
  assert.equal(rule.isRegex, true);
  assert.ok(rule.regex instanceof RegExp);
});

test('parse a cosmetic rule', () => {
  const rule = parseCosmeticRule('example.com##.ad-banner');
  assert.deepEqual(rule.domains, ['example.com']);
  assert.equal(rule.selector, '.ad-banner');
});

test('parse a global cosmetic rule', () => {
  const rule = parseCosmeticRule('##.generic-ad');
  assert.equal(rule.domains.length, 0);
  assert.equal(rule.selector, '.generic-ad');
});

test('block a matching URL', () => {
  const rs = new RuleSet().parse('||ads.example.com^');
  const res = rs.match('https://ads.example.com/banner.png');
  assert.equal(res.blocked, true);
});

test('block a subdomain of a matching domain', () => {
  const rs = new RuleSet().parse('||example.com^');
  assert.equal(rs.match('https://cdn.example.com/x.js').blocked, true);
});

test('allow rule wins over block rule', () => {
  const rs = new RuleSet().parse('||example.com^\n@@||safe.example.com^');
  assert.equal(rs.match('https://ads.example.com/x').blocked, true);
  assert.equal(rs.match('https://safe.example.com/x').blocked, false);
});

test('do not block an unrelated host', () => {
  const rs = new RuleSet().parse('||ads.example.com^');
  assert.equal(rs.match('https://goodsite.org/').blocked, false);
});

test('collect cosmetic selectors for a hostname', () => {
  const rs = new RuleSet().parse('example.com##.ad\n##.global-ad');
  const selectors = rs.cosmeticFor('www.example.com');
  assert.ok(selectors.includes('.ad'));
  assert.ok(selectors.includes('.global-ad'));
});

test('the built-in filter list parses without errors', () => {
  const rs = new RuleSet().parse(DEFAULT_FILTERS);
  assert.ok(rs.stats.network > 30, `expected many network rules, got ${rs.stats.network}`);
  assert.ok(rs.stats.cosmetic > 5, `expected cosmetic rules, got ${rs.stats.cosmetic}`);
});

test('adblocker classifies simple domain rules as native', () => {
  const rs = new RuleSet().parse('||ads.example.com^');
  const blocker = new AdBlocker({ ruleSet: rs });
  assert.equal(blocker.requiresFetch, false, 'pure domain rules should not need Fetch interception');
  assert.ok(blocker._nativePatterns.size > 0);
});

test('adblocker requires Fetch for complex rules', () => {
  const rs = new RuleSet().parse('/ads[0-9]+/');
  const blocker = new AdBlocker({ ruleSet: rs });
  assert.equal(blocker.requiresFetch, true);
});

test('adblocker blocks via the native pattern list', () => {
  const rs = new RuleSet().parse('||ads.example.com^');
  const blocker = new AdBlocker({ ruleSet: rs });
  const res = blocker.match('https://ads.example.com/x.js');
  assert.equal(res.blocked, true);
});

test('adblocker passes normal traffic through', () => {
  const rs = new RuleSet().parse('||ads.example.com^');
  const blocker = new AdBlocker({ ruleSet: rs });
  assert.equal(blocker.match('https://example.org/page').blocked, false);
});

test('a disabled adblocker blocks nothing', () => {
  const rs = new RuleSet().parse('||ads.example.com^');
  const blocker = new AdBlocker({ ruleSet: rs, enabled: false });
  assert.equal(blocker.match('https://ads.example.com/x.js').blocked, false);
});

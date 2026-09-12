const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../../MultiImageClient/Ui/wwwroot');
const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
const schema = require(path.join(root, 'personal-config.js'));

function context(failedAsset) {
  const values = new Map();
  const ctx = vm.createContext({ console, localStorage: {
    getItem: key => values.get(key) ?? null,
    setItem: (key, value) => values.set(key, String(value)),
  }, fetch: async url => ({
    ok: url !== failedAsset, status: url === failedAsset ? 404 : 200,
    text: async () => fs.readFileSync(path.join(root, url), 'utf8'),
  }) });
  ctx.window = ctx;
  for (const file of ['mcphee/vendor/typo/typo.min.js', 'mcphee/mcphee.js']) {
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), ctx);
  }
  vm.runInContext(source.slice(source.indexOf('function requireConfigObject('),
    source.indexOf('function parseConfigStorageJson(')), ctx);
  vm.runInContext(source.slice(source.indexOf('function normalizeImportedSpelling('),
    source.indexOf('function normalizeImportedInspirationLibrary(')), ctx);
  return ctx;
}

function spelling(ruleOverrides = {}) {
  return { enabled: true, customDictionary: ['myword'], ignoredWords: ['ignoredword'],
    notRareWords: ['specialword'], formality: 'standard', ruleOverrides };
}

async function create(ctx) {
  const options = source.match(/McPhee\.create\((\{[\s\S]*?\})\);/)[1];
  ctx.mcpheeJargon = [];
  return vm.runInContext(`McPhee.create(${options})`, ctx);
}

test('composer loads version 3.11.2 and selects the 2026 dictionary', async () => {
  const ctx = context();
  assert.equal(ctx.McPhee.version, '3.11.2');
  const checker = await create(ctx);
  const active = checker.resolveCheckers();
  assert.equal(active.find(c => c.id === 'spell2026').enabled, true);
  assert.equal(active.find(c => c.id === 'spell').enabled, false);
  assert.ok(checker.freqRank.size > 10000);
  assert.ok(!checker.analyze('online now.').some(i => i.classification === 'misspelled'));
  assert.ok(checker.analyze('teh cat.').some(i => i.value === 'teh' && i.classification === 'misspelled'));
  assert.ok(!checker.analyze('online amongst friends.', {
    checkers: { spell: { enabled: true }, spell2026: { enabled: true } },
  }).some(i => i.classification === 'misspelled'));
});

test('missing 2026 dictionary rejects initialization', async () => {
  await assert.rejects(create(context('mcphee/vendor/typo/en_US_2026.dic')), /HTTP 404/);
});

test('legacy and per-checker settings survive canonical configuration round trips', () => {
  const ctx = context();
  for (const overrides of [{}, { rules: { echo: false }, params: { obscureRank: 12000 } }, {
    rules: { unknown: false }, params: { echoCommonRank: 3000 },
    checkers: { spell2026: { enabled: false }, spell: { enabled: true, order: 0 },
      echo: { order: 9, params: { echoWindowWords: 7, echoCommonRank: 4000 } },
      obscureRepeat: { enabled: true, params: { obscureRank: 15000 } } },
  }]) {
    const value = spelling(overrides);
    const fields = [{ name: 'spelling', scope: 'browser', read: () => value,
      normalize: ctx.normalizeImportedSpelling }];
    const document = schema.build(fields);
    assert.equal(JSON.stringify(schema.normalize(JSON.parse(JSON.stringify(document)), fields)),
      JSON.stringify(document));
  }
});

test('invalid checker settings fail without removing valid stored settings', () => {
  const ctx = context();
  for (const checkers of [null, [], { invented: {} }, { echo: null }, { echo: [] },
    { echo: { invented: true } }, { echo: { enabled: 'yes' } },
    { echo: { order: -1 } }, { echo: { order: 0.5 } }, { echo: { order: Infinity } },
    { echo: { params: [] } }, { echo: { params: { obscureRank: 10000 } } },
    { echo: { params: { echoWindowWords: 0 } } }, { echo: { params: { echoWindowWords: 2.5 } } },
    { echo: { params: { echoWindowWords: 1000001 } } }]) {
    const value = spelling({ checkers });
    const before = JSON.stringify(value);
    assert.throws(() => ctx.normalizeImportedSpelling(value), /spelling\.ruleOverrides/);
    assert.equal(JSON.stringify(value), before);
  }
});

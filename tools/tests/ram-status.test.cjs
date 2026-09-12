const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../MultiImageClient/Ui/wwwroot/app.js'), 'utf8');
const section = source.slice(source.indexOf('const ramStatusSamples = []'), source.indexOf('async function ensureArchiveDayLoaded'));

async function render(snapshot) {
  const classes = new Set(['warn']);
  const node = {textContent: '', title: 'old measurement', classList: {
    toggle(name, active) { active ? classes.add(name) : classes.delete(name); },
    remove(name) { classes.delete(name); },
  }};
  const context = vm.createContext({el: () => node, apiUrl: x => x,
    fetch: async () => ({ok: true, status: 200, json: async () => snapshot})});
  vm.runInContext(section, context);
  await vm.runInContext('pollRamStatus()', context);
  return {node, classes};
}

test('uncapped desktop memory does not count other applications as server RAM', async () => {
  const {node} = await render({workingSetBytes: 150 * 1024 ** 2, cgroupCurrentBytes: 17 * 1024 ** 3});
  assert.match(node.textContent, /^RAM 150M/);
  assert.match(node.title, /displayed usage: server process/);
  assert.match(node.title, /control group total 17.00G/);
});

test('a capped service measures group usage against its limit', async () => {
  const {node, classes} = await render({workingSetBytes: 150 * 1024 ** 2,
    cgroupCurrentBytes: 900 * 1024 ** 2, cgroupHighBytes: 1024 ** 3});
  assert.match(node.textContent, /^RAM 900M/);
  assert.match(node.textContent, /limit 1.00G/);
  assert.equal(classes.has('warn'), true);
});

test('missing group usage does not substitute process usage or leave stale warnings', async () => {
  const {node, classes} = await render({workingSetBytes: 150 * 1024 ** 2, cgroupHighBytes: 1024 ** 3});
  assert.equal(node.textContent, 'RAM ?');
  assert.equal(node.title, 'Server memory measurement unavailable');
  assert.equal(classes.has('warn'), false);
});

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const terminalHtml = path.join(here, '..', 'Resources', 'Terminal', 'web', 'terminal.html');
const source = fs.readFileSync(terminalHtml, 'utf8');

function loadCtrlEnterNewlineData() {
  const match = source.match(/function ctrlEnterNewlineData\(entry\) \{[\s\S]*?\n  \}/);
  assert.ok(match, 'ctrlEnterNewlineData must exist');

  const context = {};
  vm.createContext(context);
  vm.runInContext(match[0], context);
  return vm.runInContext('ctrlEnterNewlineData', context);
}

test('modified Enter uses a real newline for queue-aware terminal apps', () => {
  const newlineData = loadCtrlEnterNewlineData();

  assert.equal(newlineData({ agent: 'gajae' }), '\n');
  assert.equal(newlineData({ agent: 'devezvibe' }), '\n');
});

test('modified Enter keeps the existing escape sequence for other terminals', () => {
  const newlineData = loadCtrlEnterNewlineData();

  assert.equal(newlineData({ agent: 'claude' }), '\x1b\r');
  assert.equal(newlineData({ agent: 'opencode' }), '\x1b\r');
  assert.equal(newlineData(null), '\x1b\r');
});

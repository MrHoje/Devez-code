import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const terminalHtml = path.join(here, '..', 'Resources', 'Terminal', 'web', 'terminal.html');
const source = fs.readFileSync(terminalHtml, 'utf8');

function loadHelpers() {
  const names = [
    'isHanjaConversionKey',
    'isImeModeKey',
    'armHanjaCommitSkip',
    'takeHanjaCommitSkip',
  ];
  const helpers = names.map((name) => {
    const match = source.match(new RegExp(`function ${name}\\([^)]*\\) \\{[\\s\\S]*?\\n  \\}`));
    assert.ok(match, `${name} must exist`);
    return match[0];
  }).join('\n');
  const context = {};
  vm.createContext(context);
  vm.runInContext(helpers, context);
  return Object.fromEntries(names.map((name) => [name, vm.runInContext(name, context)]));
}

test('the measured Korean Hanja key is kept out of xterm composition finalization', () => {
  const { isImeModeKey } = loadHelpers();
  assert.equal(isImeModeKey({
    key: 'Process', code: 'ControlRight', keyCode: 229, isComposing: true,
  }), true);
});

test('ordinary right Control and Korean letter composition remain normal input', () => {
  const { isImeModeKey } = loadHelpers();
  assert.equal(isImeModeKey({
    key: 'Control', code: 'ControlRight', keyCode: 17, isComposing: false,
  }), false);
  assert.equal(isImeModeKey({
    key: 'Process', code: 'KeyA', keyCode: 229, isComposing: true,
  }), false);
});

test('standard Korean language-key reports stay supported', () => {
  const { isImeModeKey } = loadHelpers();
  assert.equal(isImeModeKey({ key: 'HanjaMode', code: '', keyCode: 25 }), true);
  assert.equal(isImeModeKey({ key: '', code: 'Lang2', keyCode: 0 }), true);
});

test('opening Hanja candidates skips only the immediate commit of the shown jamo', () => {
  const { armHanjaCommitSkip, takeHanjaCommitSkip } = loadHelpers();
  const entry = { _latestImePreedit: 'ㅁ' };
  const hanja = {
    key: 'Process', code: 'ControlRight', keyCode: 229, isComposing: true,
  };

  armHanjaCommitSkip(entry, hanja, 1000);
  assert.equal(takeHanjaCommitSkip(entry, 'ㅁ', 1002), true);
  assert.equal(takeHanjaCommitSkip(entry, '※', 1100), false);
});

test('a mismatched or late composition commit is never skipped', () => {
  const { armHanjaCommitSkip, takeHanjaCommitSkip } = loadHelpers();
  const hanja = {
    key: 'Process', code: 'ControlRight', keyCode: 229, isComposing: true,
  };

  const changed = { _latestImePreedit: 'ㅁ' };
  armHanjaCommitSkip(changed, hanja, 1000);
  assert.equal(takeHanjaCommitSkip(changed, '※', 1002), false);

  const late = { _latestImePreedit: 'ㅁ' };
  armHanjaCommitSkip(late, hanja, 1000);
  assert.equal(takeHanjaCommitSkip(late, 'ㅁ', 1200), false);
});

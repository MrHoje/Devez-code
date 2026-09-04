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
  const constant = source.match(/const DEVEZVIBE_TRAILING_CELLS = \d+;/);
  assert.ok(constant, 'DEVEZVIBE_TRAILING_CELLS must exist');
  const patterns = {
    cellWidthPx: /function cellWidthPx\([^)]*\) \{[\s\S]*?\n  \}/,
    syncDevezVibeSidePadding: /function syncDevezVibeSidePadding\([^)]*\) \{[\s\S]*?\n  \}/
  };
  const names = Object.keys(patterns);
  const helpers = names.map((name) => {
    const match = source.match(patterns[name]);
    assert.ok(match, name + ' must exist');
    return match[0];
  }).join('\n');
  const context = {};
  vm.createContext(context);
  vm.runInContext(constant[0] + '\n' + helpers, context);
  return {
    trailingCells: vm.runInContext('DEVEZVIBE_TRAILING_CELLS', context),
    ...Object.fromEntries(names.map((name) => [name, vm.runInContext(name, context)]))
  };
}

function entryWith({ agent = 'devezvibe', clientWidth = 1000, cellWidth = 10.4, external = false } = {}) {
  return {
    agent,
    external,
    el: { clientWidth, style: { paddingLeft: '' } },
    term: { _core: { _renderService: { dimensions: { css: { cell: { width: cellWidth } } } } } }
  };
}

const { syncDevezVibeSidePadding, trailingCells } = loadHelpers();

test('Devez Vibe 세션의 좌우 여백이 같은 폭으로 맞는다', () => {
  for (const clientWidth of [640, 801, 1000, 1024.5, 1366, 1920]) {
    for (const cellWidth of [7.2, 9.6, 10.4, 13.5]) {
      const entry = entryWith({ clientWidth, cellWidth });
      syncDevezVibeSidePadding(entry);
      const left = Number.parseFloat(entry.el.style.paddingLeft);
      const content = clientWidth - left;
      const columns = Math.floor(content / cellWidth);
      const right = content - columns * cellWidth + trailingCells * cellWidth;
      assert.ok(Math.abs(right - left) < 0.02,
        'left=' + left + ' right=' + right + ' box=' + clientWidth + ' cell=' + cellWidth);
    }
  }
});

test('Devez Vibe 세션이 아니면 패딩을 건드리지 않는다', () => {
  const entry = entryWith({ agent: 'claude' });
  syncDevezVibeSidePadding(entry);
  assert.equal(entry.el.style.paddingLeft, '');
});

test('외부 미러와 크기 미확정 상태에서는 패딩을 건드리지 않는다', () => {
  const mirrored = entryWith({ external: true });
  syncDevezVibeSidePadding(mirrored);
  assert.equal(mirrored.el.style.paddingLeft, '');

  const unmeasured = entryWith({ cellWidth: 0 });
  syncDevezVibeSidePadding(unmeasured);
  assert.equal(unmeasured.el.style.paddingLeft, '');

  const collapsed = entryWith({ clientWidth: 0 });
  syncDevezVibeSidePadding(collapsed);
  assert.equal(collapsed.el.style.paddingLeft, '');
});

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
  const names = ['imeCellWidth', 'inlinePreeditCellRange', 'inlinePreeditUnderlineRect'];
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

function nestedFunctionSource(name) {
  const match = source.match(new RegExp(`    function ${name}\\([^)]*\\) \\{[\\s\\S]*?\\n    \\}`));
  assert.ok(match, `${name} must exist inside the IME controller`);
  return match[0];
}

function terminalWithLine({ text = '가', start = 6, cursorX = 8, cursorY = 4, rows = 10 } = {}) {
  const cells = new Map();
  let column = start;
  for (const ch of Array.from(text)) {
    cells.set(column, ch);
    column += /[\u1100-\u115f\u2e80-\ua4cf\uac00-\ud7a3\uf900-\ufaff\ufe30-\ufe4f\uff00-\uff60\uffe0-\uffe6]/u.test(ch) ? 2 : 1;
  }
  return {
    cols: 20,
    rows,
    buffer: {
      active: {
        cursorX,
        cursorY,
        viewportY: 2,
        getLine(index) {
          assert.equal(index, cursorY + 2);
          return { getCell: col => ({ getChars: () => cells.get(col) || '' }) };
        },
      },
    },
  };
}

test('v2 is accepted beside v1 without changing other OSC payloads', () => {
  assert.match(source, /\^devez-preedit-v\(\[12\]\);\(\[01\]\)\$/);
  assert.match(source, /devezInlinePreeditVersion/);
  assert.match(source, /hostDecoratesInlinePreedit\(\)/);
});

test('the underline is anchored only after the rendered preedit matches the cursor', () => {
  const { inlinePreeditCellRange } = loadHelpers();
  const actual = inlinePreeditCellRange(terminalWithLine(), '가', 1);
  assert.equal(JSON.stringify(actual), JSON.stringify({ row: 4, start: 6, end: 8 }));

  assert.equal(inlinePreeditCellRange(terminalWithLine({ text: '나' }), '가', 1), null);
  assert.equal(inlinePreeditCellRange(terminalWithLine({ cursorX: 7 }), '가', 1), null);
  assert.equal(inlinePreeditCellRange(terminalWithLine({ cursorY: 9 }), '가', 1), null);
});

test('the underline occupies the last physical pixel at common display scales', () => {
  const { inlinePreeditUnderlineRect } = loadHelpers();
  const range = { row: 4, start: 6, end: 8 };
  for (const dpr of [1, 1.25, 1.5, 2]) {
    const rect = inlinePreeditUnderlineRect(range, 200, 200, 20, 10, dpr);
    const expectedBottom = Math.round(100 * dpr) / dpr;
    assert.ok(Math.abs(rect.top + rect.height - expectedBottom) < 1e-9);
    assert.ok(Math.abs(rect.height * dpr - 1) < 1e-9);
    assert.equal(rect.left, 60);
    assert.equal(rect.width, 20);
  }
});

test('the custom line is isolated from xterm ANSI underline rendering', () => {
  assert.match(source, /\.ime-inline-preedit-underline\s*\{/);
  assert.match(source, /background:\s*var\(--term-fg/);
  assert.match(source, /pointer-events:\s*none/);
  assert.match(source, /updateInlinePreeditUnderline\(\)/);
});

test('transient frames and chained Hangul syllables keep the last validated underline', () => {
  const schedule = nestedFunctionSource('schedulePreedit');
  const update = nestedFunctionSource('updateInlinePreeditUnderline');
  assert.match(schedule, /if \(pendingPreedit\) cancelInlinePreeditUnderlineClear\(\);/);
  assert.match(schedule, /else scheduleInlinePreeditUnderlineClear\(\);/);
  assert.match(nestedFunctionSource('scheduleInlinePreeditUnderlineClear'), /requestAnimationFrame/);
  assert.match(update, /if \(!rect\) return;/);
  assert.doesNotMatch(update, /if \(!rect\)\s*\{[\s\S]*?clearInlinePreeditUnderline/);

  const composerCols = source.indexOf('const composerCols = syncDevezVibeComposerLayout();');
  assert.notEqual(composerCols, -1);
  const compositionEnd = source.lastIndexOf("ta.addEventListener('compositionend', function (e) {", composerCols);
  assert.notEqual(compositionEnd, -1);
  const clearFrame = source.indexOf("schedulePreedit('');", compositionEnd);
  assert.notEqual(clearFrame, -1);
  assert.doesNotMatch(source.slice(clearFrame, clearFrame + 220), /clearInlinePreeditUnderline\(\)/);
});

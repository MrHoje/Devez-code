import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const web = join(dirname(dirname(fileURLToPath(import.meta.url))), 'Resources', 'Terminal', 'web');
const { Terminal } = require(join(web, 'xterm6.min.js'));
const corners = require(join(web, 'devez-question-corners.js'));
const line = '\x1b[38;2;52;211;199m';
const fill = '\x1b[48;2;54;54;54m';
const half = '\x1b[38;2;54;54;54m\x1b[49m';
const sample = '\x1b[1;3H' + line + fill + '▖' + half + '▄▄' +
  '\x1b[2;3H' + line + fill + '▌ 답변' +
  '\x1b[3;3H' + line + fill + '▘' + half + '▀▀\x1b[0m';
const write = (term, data) => new Promise(resolve => term.write(data, resolve));
const makeTerm = () => new Terminal({ cols: 20, rows: 6, scrollback: 50, allowProposedApi: true,
  theme: { background: '#1f1f1e' } });

test('real xterm normal and alternate buffers identify only the paired corners', async () => {
  for (const alternate of [false, true]) {
    const term = makeTerm();
    if (alternate) await write(term, '\x1b[?1049h');
    await write(term, sample);
    const found = corners.findCorners(term);
    assert.deepEqual(found, [
      { row: 0, col: 2, top: true, line: '#34d3c7', fill: '#363636', outside: '#1f1f1e' },
      { row: 2, col: 2, top: false, line: '#34d3c7', fill: '#363636', outside: '#1f1f1e' }
    ]);
    assert.equal(term.buffer.active.getLine(1).translateToString(true).trim(), '▌ 답변');
    await write(term, '\x1b[1;3H\x1b[0m일반 문자\x1b[3;3H일반 문자');
    assert.deepEqual(corners.findCorners(term), []);
    term.dispose();
  }
});

test('scrolling, erase and buffer switches cannot leave old corner positions', async () => {
  const term = makeTerm();
  await write(term, sample);
  await write(term, '\x1b[6;1H' + '\r\n'.repeat(8));
  assert.deepEqual(corners.findCorners(term), []);
  term.scrollToTop();
  assert.equal(corners.findCorners(term).length, 2);
  await write(term, '\x1b[?1049h');
  assert.deepEqual(corners.findCorners(term), []);
  await write(term, sample);
  assert.equal(corners.findCorners(term).length, 2);
  await write(term, '\x1b[2J');
  assert.deepEqual(corners.findCorners(term), []);
  term.dispose();
});

test('standalone quadrants, different colours and inverse text are preserved', async () => {
  for (const change of [
    '\x1b[1;4H\x1b[0mA',
    '\x1b[1;3H\x1b[38;2;1;2;3m' + fill + '▖',
    '\x1b[1;3H\x1b[7m' + line + fill + '▖\x1b[27m'
  ]) {
    const term = makeTerm();
    await write(term, sample + change);
    assert.equal(corners.findCorners(term).some(corner => corner.top), false);
    term.dispose();
  }
});

function browserHarness(real) {
  const frames = new Map();
  let frameId = 0;
  class Element {
    constructor() { this.style = {}; this.children = []; this.ownerDocument = doc; }
    setAttribute(name, value) { this[name] = value; }
    appendChild(child) { this.children.push(child); child.parent = this; }
    remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
    getBoundingClientRect() { return this.rect; }
  }
  const doc = { createElement: () => new Element(), defaultView: {
    requestAnimationFrame: callback => { const id = ++frameId; frames.set(id, callback); return id; },
    cancelAnimationFrame: id => frames.delete(id)
  }};
  const screen = new Element();
  screen.rect = { width: 200, height: 120 };
  const events = new Map();
  const subscribe = name => callback => {
    events.set(name, callback);
    return { dispose: () => events.delete(name) };
  };
  const term = {
    buffer: real.buffer, options: real.options, cols: real.cols, rows: real.rows,
    element: { querySelector: () => screen }, selected: false, modes: { synchronizedOutputMode: false },
    hasSelection() { return this.selected; },
    onRender: subscribe('render'), onResize: subscribe('resize'),
    onScroll: subscribe('scroll'), onSelectionChange: subscribe('selection'),
    loadAddon(addon) { this.addon = addon; addon.activate(this); }
  };
  return { term, screen, events, frames, flush() {
    const pending = [...frames.values()]; frames.clear(); pending.forEach(callback => callback());
  }};
}

test('clipped answer corners stay patched when the body is hidden by a panel or viewport edge', async t => {
  for (const top of [false, true]) {
    for (const row of [0, 2, 5]) {
      const real = makeTerm();
      t.after(() => real.dispose());
      await write(real, '\x1b[?1049h');
      await write(real, '\x1b[2;1H\x1b[0m└─────────────┘');
      await write(real, `\x1b[${row + 1};3H` + line + fill + (top ? '▖' : '▘') +
        half + (top ? '▄▄' : '▀▀') + '\x1b[0m');
      const before = real.buffer.active.getLine(row).translateToString();
      const ui = browserHarness(real);
      corners.install(ui.term, 'devezvibe');
      ui.flush();
      const layer = ui.screen.children[0];
      assert.equal(layer.children.length, 1, `clipped ${top ? 'top' : 'bottom'} corner at row ${row}`);
      const patch = layer.children[0];
      assert.equal(patch.style.backgroundColor, '#1f1f1e');
      assert.equal(patch.style.backgroundSize, '100% 50%');
      assert.equal(patch.style.backgroundPosition, top ? 'left bottom' : 'left top');
      assert.equal(real.buffer.active.getLine(row).translateToString(), before);
      await write(real, `\x1b[${row + 1};1H\x1b[2K`);
      ui.events.get('render')(); ui.flush();
      assert.equal(layer.children.length, 0);
      ui.term.addon.dispose();
    }
  }
});

test('clipped corners reject unrelated glyphs, wrong colours and inverse cells', async t => {
  for (const change of [
    '\x1b[3;4H\x1b[0mA',
    '\x1b[3;4H' + half + '▄',
    '\x1b[3;4H\x1b[38;2;1;2;3m▀',
    '\x1b[3;4H\x1b[7m' + half + '▀',
    '\x1b[3;3H\x1b[7m' + line + fill + '▘',
    '\x1b[3;3H\x1b[0m▘',
    '\x1b[3;3H' + line + fill + '▌'
  ]) {
    const real = makeTerm();
    t.after(() => real.dispose());
    await write(real, sample + '\x1b[1;1H\x1b[0m\x1b[2K\x1b[2;1H\x1b[2K' + change);
    assert.deepEqual(corners.findCorners(real), [], JSON.stringify(change));
  }
});

test('visible patches survive body occlusion and follow scrolling and buffer switches', async t => {
  for (const alternate of [false, true]) {
    const real = makeTerm();
    t.after(() => real.dispose());
    if (alternate) await write(real, '\x1b[?1049h');
    await write(real, sample);
    const ui = browserHarness(real);
    corners.install(ui.term, 'devezvibe');
    t.after(() => ui.term.addon.dispose());
    ui.flush();
    const layer = ui.screen.children[0];
    const bottom = layer.children[1];
    // The panel covers the answer body and top edge; only the bottom half-row remains.
    await write(real, '\x1b[1;1H\x1b[0m\x1b[2K\x1b[2;1H\x1b[2K└─────────────┘');
    ui.events.get('render')(); ui.flush();
    assert.deepEqual(layer.children, [bottom]);
    assert.equal(bottom.style.top, '40px');
    assert.equal(bottom.style.backgroundPosition, 'left top');
    ui.term.selected = true;
    ui.events.get('selection')(); ui.flush();
    assert.equal(layer.children.length, 0);
    ui.term.selected = false;
    ui.events.get('selection')(); ui.flush();
    assert.equal(layer.children.length, 1);
    await write(real, '\x1b[6;1H\r\n\r\n');
    ui.events.get('scroll')(); ui.flush();
    assert.equal(layer.children.length, 1);
    assert.equal(layer.children[0].style.top, '0px');
    await write(real, '\r\n');
    ui.events.get('scroll')(); ui.flush();
    assert.equal(layer.children.length, 0);
    if (!alternate) {
      real.scrollToTop();
      ui.events.get('scroll')(); ui.flush();
      assert.equal(layer.children.length, 1);
      assert.equal(layer.children[0].style.top, '40px');
      await write(real, '\x1b[?1049h');
      ui.events.get('render')(); ui.flush();
      assert.equal(layer.children.length, 0);
      await write(real, '\x1b[?1049l');
      real.scrollToTop();
      ui.events.get('render')(); ui.flush();
      assert.equal(layer.children.length, 1);
    }
  }
});

test('pixel patches follow resize, hide, selection and dispose without changing text', async () => {
  const real = makeTerm();
  await write(real, sample);
  const ui = browserHarness(real);
  assert.equal(corners.install(ui.term, 'claude'), false);
  assert.equal(corners.install(ui.term, 'codex'), false);
  assert.equal(ui.screen.children.length, 0);
  assert.equal(corners.install(ui.term, 'devezvibe'), true);
  ui.flush();
  const layer = ui.screen.children[0];
  assert.equal(layer['aria-hidden'], 'true');
  assert.equal(layer.style.pointerEvents, 'none');
  assert.equal(layer.children.length, 2);
  const [top, bottom] = layer.children;
  assert.equal(top.style.left, '20px');
  assert.equal(top.style.width, '10px');
  assert.equal(top.style.height, '20px');
  assert.equal(top.style.backgroundColor, '#1f1f1e');
  assert.equal(top.style.backgroundSize, '100% 50%');
  assert.equal(top.style.backgroundPosition, 'left bottom');
  assert.equal(bottom.style.backgroundPosition, 'left top');
  assert.equal(bottom.style.top, '40px');
  assert.match(top.style.backgroundImage, /#34d3c7 50%,#363636 50%/);
  for (let i = 0; i < 100; i++) ui.events.get('render')();
  assert.equal(ui.frames.size, 1);
  ui.screen.rect = { width: 300, height: 180 };
  ui.events.get('resize')(); ui.flush();
  assert.equal(top.style.left, '30px');
  assert.equal(top.style.height, '30px');
  assert.equal(layer.children.length, 2);
  ui.screen.rect = { width: 0, height: 0 };
  ui.events.get('render')(); ui.flush();
  assert.equal(layer.children.length, 0);
  ui.screen.rect = { width: 200, height: 120 };
  ui.events.get('render')(); ui.flush();
  assert.equal(layer.children.length, 2);
  ui.term.selected = true;
  ui.events.get('selection')(); ui.flush();
  assert.equal(layer.children.length, 0);
  ui.term.selected = false;
  ui.events.get('selection')(); ui.flush();
  assert.equal(layer.children.length, 2);
  ui.term.modes.synchronizedOutputMode = true;
  await write(real, '\x1b[2J');
  ui.events.get('scroll')(); ui.flush();
  assert.equal(layer.children.length, 2, 'a synchronized frame keeps its old decoration until it is painted');
  ui.term.modes.synchronizedOutputMode = false;
  ui.events.get('render')(); ui.flush();
  assert.equal(layer.children.length, 0);
  ui.events.get('scroll')();
  ui.term.addon.dispose();
  assert.equal(ui.screen.children.length, 0);
  assert.equal(ui.events.size, 0);
  assert.equal(ui.frames.size, 0);
  real.dispose();
});

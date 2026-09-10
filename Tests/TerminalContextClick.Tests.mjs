import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);

const source = fs.readFileSync(new URL('../Resources/Terminal/web/terminal.html', import.meta.url), 'utf8');
function harness() {
  const sent = [], deferred = [];
  const alternate = {};
  const entry = {
    agent: 'devezvibe', roomId: 'one', devezContextClick: true, devezContextPending: 1,
    el: { querySelector: () => ({ contains: () => true,
      getBoundingClientRect: () => ({ left: 10, top: 20, right: 810, bottom: 420, width: 800, height: 400 }) }) },
    term: { cols: 80, rows: 20, buffer: { active: alternate, alternate }, hasSelection: () => false }
  };
  const ctx = { post: x => sent.push(x), setTimeout: f => deferred.push(f),
    activeRoomId: 'one', terms: { one: entry }, doCopy: () => sent.push({ type: 'copy' }) };
  vm.createContext(ctx);
  for (const name of ['handleDevezContextClick', 'handleDevezContextOsc']) {
    const match = source.match(new RegExp('  function ' + name + '\\([^]*?\\n  \\}'));
    assert.ok(match, name);
    vm.runInContext(match[0], ctx);
  }
  let prevented = 0, stopped = 0;
  const event = { clientX: 55, clientY: 165, target: {},
    preventDefault: () => prevented++, stopImmediatePropagation: () => stopped++ };
  return { entry, event, sent, ctx, flush: () => deferred.splice(0).forEach(f => f()),
    stopped: () => [prevented, stopped] };
}

test('right click sends one complete press/release pair and no immediate paste', () => {
  const h = harness();
  assert.equal(h.ctx.handleDevezContextClick(h.entry, h.event), true);
  assert.equal(h.sent.length, 1);
  assert.equal(h.sent[0].data, '\x1b[<2;5;8M\x1b[<2;5;8m');
  assert.deepEqual(h.stopped(), [1, 1]);
});

test('old clients, other agents, normal buffer, Shift and outside clicks keep fallback', () => {
  for (const change of [
    h => h.entry.devezContextClick = false,
    h => h.entry.agent = 'claude',
    h => h.entry.dead = true,
    h => h.entry.term.buffer.active = {},
    h => h.event.shiftKey = true,
    h => h.event.clientX = 810,
    h => h.event.clientY = 19,
    h => h.entry.el.querySelector = () => null
  ]) {
    const h = harness(); change(h);
    assert.equal(h.ctx.handleDevezContextClick(h.entry, h.event), false);
    assert.equal(h.sent.length, 0);
  }
});

test('only a non-link response requests paste, preserving selection copy', () => {
  for (const selected of [false, true]) {
    const h = harness();
    h.entry.term.hasSelection = () => selected;
    assert.equal(h.ctx.handleDevezContextOsc(h.entry, 'devez-context-paste-v1'), true);
    assert.equal(h.sent.length, 0);
    h.flush();
    assert.equal(h.sent.length, 1);
    assert.equal(h.sent[0].type, selected ? 'copy' : 'requestPaste');
  }
});

test('late responses cannot paste into switched, disposed or disabled sessions', () => {
  for (const change of [
    h => h.ctx.activeRoomId = 'two',
    h => h.entry.dead = true,
    h => delete h.ctx.terms.one,
    h => h.ctx.handleDevezContextOsc(h.entry, 'devez-context-v1;0')
  ]) {
    const h = harness();
    h.ctx.handleDevezContextOsc(h.entry, 'devez-context-paste-v1');
    change(h); h.flush();
    assert.equal(h.sent.length, 0);
  }
});

test('capability is explicit and limited to Devez Vibe', () => {
  const h = harness();
  assert.equal(h.ctx.handleDevezContextOsc(h.entry, 'devez-context-v1;0'), true);
  assert.equal(h.entry.devezContextClick, false);
  assert.equal(h.ctx.handleDevezContextOsc(h.entry, 'devez-context-paste-v1'), true);
  assert.equal(h.ctx.handleDevezContextOsc(h.entry, 'devez-context-v1;1'), true);
  h.entry.agent = 'codex';
  assert.equal(h.ctx.handleDevezContextOsc(h.entry, 'devez-context-v1;1'), false);
});

test('deferred paste cannot survive disable and re-enable of the same terminal', () => {
  const h = harness();
  h.ctx.handleDevezContextOsc(h.entry, 'devez-context-paste-v1');
  h.ctx.handleDevezContextOsc(h.entry, 'devez-context-v1;0');
  h.ctx.handleDevezContextOsc(h.entry, 'devez-context-v1;1');
  h.flush();
  assert.equal(h.sent.length, 0);
});

test('coordinates at screen corners remain in range under fractional scaling', () => {
  for (const [x, y, col, row] of [[10, 20, 1, 1], [810.49, 420.24, 80, 20]]) {
    const h = harness();
    h.entry.el.querySelector = () => ({ contains: () => true,
      getBoundingClientRect: () => ({ left: 10, top: 20, right: 810.5, bottom: 420.25, width: 800.5, height: 400.25 }) });
    Object.assign(h.event, { clientX: x, clientY: y });
    assert.equal(h.ctx.handleDevezContextClick(h.entry, h.event), true);
    assert.equal(h.sent[0].data, `\x1b[<2;${col};${row}M\x1b[<2;${col};${row}m`);
  }
});

test('rapid right clicks each have one release and consume one response', () => {
  const h = harness();
  h.entry.devezContextPending = 0;
  for (let i = 0; i < 20; i++) h.ctx.handleDevezContextClick(h.entry, h.event);
  assert.equal(h.entry.devezContextPending, 20);
  assert.equal(h.sent.filter(x => x.type === 'input').length, 20);
  assert.ok(h.sent.every(x => x.data === '\x1b[<2;5;8M\x1b[<2;5;8m'));
  for (let i = 0; i < 20; i++) h.ctx.handleDevezContextOsc(h.entry, 'devez-context-link-v1');
  h.ctx.handleDevezContextOsc(h.entry, 'devez-context-paste-v1');
  h.flush();
  assert.equal(h.sent.length, 20);
});

test('link acknowledgements and unsolicited or repeated paste responses do not paste', () => {
  const h = harness();
  h.ctx.handleDevezContextOsc(h.entry, 'devez-context-link-v1');
  h.ctx.handleDevezContextOsc(h.entry, 'devez-context-paste-v1');
  h.flush();
  assert.equal(h.sent.length, 0);
  h.entry.devezContextPending = 1;
  h.ctx.handleDevezContextOsc(h.entry, 'devez-context-paste-v1');
  h.ctx.handleDevezContextOsc(h.entry, 'devez-context-paste-v1');
  h.flush();
  assert.equal(h.sent.length, 1);
});

test('document handler routes before clipboard and parser/reset wiring is present', () => {
  const start = source.indexOf("document.addEventListener('mousedown', function (e) {");
  const handler = source.slice(start, source.indexOf('}, { capture: true });', start));
  assert.ok(handler.indexOf('handleDevezContextClick(t, e)') < handler.indexOf('t.term.hasSelection()'));
  assert.match(source, /registerOscHandler\(777, function \(data\) \{\s*if \(handleDevezContextOsc\(entry, data\)\)/);
  assert.match(source, /case 'restarted':[^]*?t\.devezContextClick = false/);
});

for (const engine of ['xterm.min.js', 'xterm6.min.js']) {
  test(`real ${engine} parser negotiates and delivers one paste response`, async () => {
    const { Terminal } = require(new URL('../Resources/Terminal/web/' + engine, import.meta.url).pathname.replace(/^\/(.:)/, '$1'));
    const h = harness();
    const terminal = new Terminal({ cols: 80, rows: 20, allowProposedApi: true });
    h.entry.term = terminal;
    terminal.parser.registerOscHandler(777, data => h.ctx.handleDevezContextOsc(h.entry, data));
    const write = data => new Promise(resolve => terminal.write(data, resolve));
    try {
      await write('\x1b[?1049h\x1b]777;devez-context-v1;1\x07');
      assert.equal(h.ctx.handleDevezContextClick(h.entry, h.event), true);
      await write('\x1b]777;devez-context-paste-v1\x07');
      h.flush();
      assert.deepEqual(h.sent.map(x => x.type), ['input', 'requestPaste']);
      await write('\x1b]777;devez-context-paste-v1\x07');
      h.flush();
      assert.equal(h.sent.length, 2);
      assert.equal(h.ctx.handleDevezContextClick(h.entry, h.event), true);
      await write('\x1b]777;devez-context-link-v1\x07');
      h.flush();
      assert.equal(h.sent.length, 3);
      await write('\x1b]777;devez-context-v1;0\x07\x1b[?1049l');
      assert.equal(h.ctx.handleDevezContextClick(h.entry, h.event), false);
    } finally { terminal.dispose(); }
  });
}

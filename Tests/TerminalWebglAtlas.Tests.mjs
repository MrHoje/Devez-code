import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const terminalHtml = path.join(here, '..', 'Resources', 'Terminal', 'web', 'terminal.html');
const source = fs.readFileSync(terminalHtml, 'utf8');

test('terminal inline script remains valid JavaScript', () => {
  const scripts = [...source.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g)];
  assert.ok(scripts.length > 0, 'terminal.html must contain an inline script');
  for (const script of scripts) new vm.Script(script[1]);
});

function loadGuard(terms) {
  const startMarker = '// BEGIN shared-webgl-atlas-guard';
  const endMarker = '// END shared-webgl-atlas-guard';
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker);
  assert.ok(start >= 0 && end > start, 'shared WebGL atlas guard block must exist');

  const frames = [];
  const logs = [];
  const context = {
    terms,
    Math,
    requestAnimationFrame: callback => frames.push(callback),
    dlog: message => logs.push(message),
  };
  vm.createContext(context);
  vm.runInContext(source.slice(start + startMarker.length, end), context);
  return {
    frames,
    logs,
    guard: vm.runInContext('guardSharedWebglAtlas', context),
  };
}

test('shared atlas clear refreshes every WebGL terminal on the same xterm engine', () => {
  const refreshes = { source: 0, sibling: 0, xterm5: 0, dom: 0 };
  const terms = {
    source: { engine6: true, webgl: {}, dead: false, term: { rows: 42, refresh: () => refreshes.source++ } },
    sibling: { engine6: true, webgl: {}, dead: false, term: { rows: 42, refresh: () => refreshes.sibling++ } },
    xterm5: { engine6: false, webgl: {}, dead: false, term: { rows: 42, refresh: () => refreshes.xterm5++ } },
    dom: { engine6: true, webgl: null, dead: false, term: { rows: 42, refresh: () => refreshes.dom++ } },
  };
  let nativeClears = 0;
  const renderer = { clearTextureAtlas: () => nativeClears++ };
  const loaded = loadGuard(terms);

  loaded.guard(terms.source, { _renderer: renderer }, 'source');
  renderer.clearTextureAtlas();
  renderer.clearTextureAtlas();

  assert.equal(nativeClears, 2, 'the original atlas clear must always run');
  assert.equal(loaded.frames.length, 1, 'repeated clears in one frame must coalesce');
  loaded.frames.shift()();
  assert.deepEqual(refreshes, { source: 1, sibling: 1, xterm5: 0, dom: 0 });
  assert.match(loaded.logs.at(-1), /reason=clear rooms=2/);
});

test('the guard is attached immediately after the WebGL addon loads', () => {
  assert.match(source, /term\.loadAddon\(webgl\);\s*entry\.webgl = webgl;\s*guardSharedWebglAtlas\(entry, webgl, roomId\);\s*scheduleSharedWebglRefresh\(entry\.engine6, roomId, 'attach'\);/);
});

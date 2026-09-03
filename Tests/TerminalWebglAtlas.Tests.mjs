import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.join(here, '..', 'Resources', 'Terminal', 'web');
const terminalHtml = path.join(webDir, 'terminal.html');
const source = fs.readFileSync(terminalHtml, 'utf8');

const betaBundles = {
  'xterm6.min.js': '7aa2c68ddaade0bdd0f7fe58b9549ba82c8303bd508ece2f7f251d6391ec1cc1',
  'addon-fit6.min.js': 'acc70dbdb41ff8e6cb8b37ad888dabd2285267e97f79f2e72ab864fd6337df1c',
  'addon-webgl6.min.js': '437c85c682b25f9cc0741d0665c383eddb4fe409a6f8ec422afbfb5ad3d559a8',
};

test('terminal inline script remains valid JavaScript', () => {
  const scripts = [...source.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g)];
  assert.ok(scripts.length > 0, 'terminal.html must contain an inline script');
  for (const script of scripts) new vm.Script(script[1]);
});

test('xterm6 uses the pinned compatible beta bundle set with the atlas fixes', () => {
  assert.match(source, /core: '6\.1\.0-beta\.302', fit: '0\.12\.0-beta\.299', webgl: '0\.20\.0-beta\.298'/);
  assert.match(source, /const use6 = \(agent === 'codex' \|\| agent === 'devezvibe'\) && window\.Terminal6;/,
    'only codex and devezvibe may select xterm6');
  assert.match(source, /window\.Terminal = window\.__t5\.Terminal; window\.FitAddon = window\.__t5\.FitAddon;/,
    'the default agent globals must return to xterm5');
  for (const [name, expectedHash] of Object.entries(betaBundles)) {
    const bundle = fs.readFileSync(path.join(webDir, name));
    const canonicalBundle = Buffer.from(bundle.toString('utf8').replaceAll('\r\n', '\n'));
    assert.equal(crypto.createHash('sha256').update(canonicalBundle).digest('hex'), expectedHash, `${name} hash`);
    new vm.Script(bundle.toString('utf8'), { filename: name });
  }
  const webgl = fs.readFileSync(path.join(webDir, 'addon-webgl6.min.js'), 'utf8');
  assert.match(webgl, /pageLayoutVersion/, 'shared renderers must observe every atlas layout change');
  assert.match(webgl, /_evictAllPages/, 'atlas pages must be evicted before exceeding texture capacity');
  assert.match(webgl, /maxAtlasPages/, 'the renderer texture page limit must be enforced');
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

test('legacy shared atlas clear refreshes every xterm5 WebGL terminal', () => {
  const refreshes = { source: 0, sibling: 0, xterm6: 0, dom: 0 };
  const terms = {
    source: { engine6: false, webgl: {}, dead: false, term: { rows: 42, refresh: () => refreshes.source++ } },
    sibling: { engine6: false, webgl: {}, dead: false, term: { rows: 42, refresh: () => refreshes.sibling++ } },
    xterm6: { engine6: true, webgl: {}, dead: false, term: { rows: 42, refresh: () => refreshes.xterm6++ } },
    dom: { engine6: false, webgl: null, dead: false, term: { rows: 42, refresh: () => refreshes.dom++ } },
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
  assert.deepEqual(refreshes, { source: 1, sibling: 1, xterm6: 0, dom: 0 });
  assert.match(loaded.logs.at(-1), /reason=clear rooms=2/);
});

test('xterm6 beta does not receive the legacy atlas monkey patch', () => {
  let nativeClears = 0;
  const renderer = { clearTextureAtlas: () => nativeClears++ };
  const entry = { engine6: true, webgl: {}, dead: false, term: { rows: 42, refresh: () => assert.fail('unexpected refresh') } };
  const loaded = loadGuard({ beta: entry });

  loaded.guard(entry, { _renderer: renderer }, 'beta');
  renderer.clearTextureAtlas();

  assert.equal(nativeClears, 1);
  assert.equal(renderer._devezAtlasGuard, undefined);
  assert.equal(loaded.frames.length, 0);
});

test('mouse compatibility routes legacy and xterm6 beta services correctly', () => {
  const startMarker = '// BEGIN xterm-mouse-compat';
  const endMarker = '// END xterm-mouse-compat';
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker);
  assert.ok(start >= 0 && end > start, 'xterm mouse compatibility block must exist');

  const context = {};
  vm.createContext(context);
  vm.runInContext(source.slice(start + startMarker.length, end), context);
  const state = vm.runInContext('terminalMouseState', context);
  const trigger = vm.runInContext('triggerTerminalMouseEvent', context);
  const event = { button: 4, action: 0 };

  let legacyEvent;
  const legacyService = { triggerMouseEvent: value => (legacyEvent = value, true) };
  const legacy = { term: { _core: { coreMouseService: legacyService } } };
  assert.equal(state(legacy), legacyService);
  assert.equal(trigger(legacy, event), true);
  assert.equal(legacyEvent, event);

  let betaEvent;
  const betaState = { activeProtocol: 'VT200' };
  const beta = { term: { _core: {
    mouseStateService: betaState,
    _mouseService: { _triggerMouseEvent: value => (betaEvent = value, true) },
  } } };
  assert.equal(state(beta), betaState);
  assert.equal(trigger(beta, event), true);
  assert.equal(betaEvent, event);
});

test('the guard is attached immediately after the WebGL addon loads', () => {
  assert.match(source, /term\.loadAddon\(webgl\);\s*entry\.webgl = webgl;\s*guardSharedWebglAtlas\(entry, webgl, roomId\);\s*scheduleSharedWebglRefresh\(entry\.engine6, roomId, 'attach'\);/);
});

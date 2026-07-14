(function () {
  let editor = null, pending = null, ready = false;
  function post(o){ if(window.chrome&&window.chrome.webview) window.chrome.webview.postMessage(JSON.stringify(o)); }

  function boot() {
    if (typeof require === 'undefined') { document.getElementById('err').style.display='block'; return; }
    require.config({ paths: { vs: 'vs' } });
    require(['vs/editor/editor.main'], function () {
      editor = monaco.editor.createDiffEditor(document.getElementById('c'), {
        readOnly: true, automaticLayout: true, renderSideBySide: true,
        minimap: { enabled: true }, scrollBeyondLastLine: false
      });
      ready = true;
      post({ type: 'pageReady' });
      if (pending) { apply(pending); pending = null; }
    });
  }

  function apply(m) {
    if (!ready) { pending = m; return; }
    if (m.type === 'setDiff') {
      const lang = m.language || 'plaintext';
      editor.setModel({
        original: monaco.editor.createModel(m.originalText || '', lang),
        modified: monaco.editor.createModel(m.modifiedText || '', lang),
      });
    } else if (m.type === 'setTheme') {
      monaco.editor.defineTheme('devez', { base: m.base || 'vs-dark', inherit: true,
        rules: m.rules || [], colors: m.colors || {} });
      monaco.editor.setTheme('devez');
    }
  }

  window.chrome && window.chrome.webview &&
    window.chrome.webview.addEventListener('message', e => {
      try { apply(JSON.parse(e.data)); } catch (_) {}
    });

  // vs/loader.js 동적 로드 후 boot
  const s = document.createElement('script');
  s.src = 'vs/loader.js';
  s.onload = boot;
  s.onerror = function(){ document.getElementById('err').style.display='block'; };
  document.head.appendChild(s);
})();

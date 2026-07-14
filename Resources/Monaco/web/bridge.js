(function () {
  let editor = null, pending = null, ready = false;
  // WebView2 는 객체 기반 메시징: 객체를 그대로 postMessage 하면 호스트가 WebMessageAsJson 으로 받는다.
  // JSON.stringify 하면 "문자열"이 되어 호스트에서 이중 인코딩되고 파싱 실패로 조용히 버려진다.
  function post(o){ if(window.chrome&&window.chrome.webview) window.chrome.webview.postMessage(o); }
  function reportErr(where, err){
    var msg = (err && (err.stack || err.message)) ? (err.stack || err.message) : String(err);
    post({ type: 'jsError', where: where, message: msg });
  }
  // 전역 JS 에러 → 네이티브 diag 로 보고
  window.addEventListener('error', function(e){ reportErr('window.onerror', e.error || e.message); });
  window.addEventListener('unhandledrejection', function(e){ reportErr('unhandledrejection', e.reason); });

  function boot() {
    try {
      if (typeof require === 'undefined') { post({ type:'jsError', where:'boot', message:'require undefined (loader.js 로드 실패)' }); document.getElementById('err').style.display='block'; return; }
      require.config({ paths: { vs: 'vs' } });
      require(['vs/editor/editor.main'], function () {
        try {
          editor = monaco.editor.createDiffEditor(document.getElementById('c'), {
            readOnly: true, automaticLayout: true, renderSideBySide: true,
            minimap: { enabled: true }, scrollBeyondLastLine: false,
            renderOverviewRuler: true,          // 좌/우 오버뷰 룰러(변경 위치 빨강/초록)
            renderMarginRevertIcon: false,      // 읽기전용 — revert 아이콘 숨김
            scrollbar: { verticalScrollbarSize: 14, horizontalScrollbarSize: 14, useShadows: false }
          });
          ready = true;
          post({ type: 'pageReady' });
          if (pending) { apply(pending); pending = null; }
        } catch (err) { reportErr('createDiffEditor', err); }
      }, function (err) { reportErr('require(editor.main)', err); });
    } catch (err) { reportErr('boot', err); }
  }

  function apply(m) {
    if (!ready) { pending = m; return; }
    try {
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
    } catch (err) { reportErr('apply:' + (m && m.type), err); }
  }

  window.chrome && window.chrome.webview &&
    window.chrome.webview.addEventListener('message', e => {
      // 호스트가 PostWebMessageAsJson 으로 보내면 e.data 는 이미 파싱된 객체다(JSON.parse 금지).
      try { apply(e.data); } catch (err) { reportErr('onmessage', err); }
    });

  // vs/loader.js 동적 로드 후 boot
  const s = document.createElement('script');
  s.src = 'vs/loader.js';
  s.onload = boot;
  s.onerror = function(){ post({ type:'jsError', where:'loader.js', message:'vs/loader.js onerror (404?)' }); document.getElementById('err').style.display='block'; };
  document.head.appendChild(s);
})();

(function () {
  let editor = null, pending = null, ready = false;
  // 각 패널(좌=원본/우=수정) 자체 스크롤바 오버뷰 룰러에 자기쪽 변경을 직접 표시(VS 스타일).
  // Monaco 통합 룰러(renderOverviewRuler)는 우측끝에 몰아 그리므로 끄고, 서브에디터에 데코레이션을 건다.
  let insColor = '#3FB950', remColor = '#F85149';   // 추가=초록 / 삭제=빨강 (setTheme 로 갱신)
  let oDecoIds = [], mDecoIds = [];

  function applyRulers() {
    if (!editor) return;
    try {
      const oEd = editor.getOriginalEditor && editor.getOriginalEditor();
      const mEd = editor.getModifiedEditor && editor.getModifiedEditor();
      if (!oEd || !mEd) return;
      let changes = null;
      try { changes = editor.getLineChanges(); } catch (e) {}
      if (!changes && editor.getDiffComputationResult) {
        const res = editor.getDiffComputationResult();
        changes = res && res.changes;
      }
      changes = changes || [];
      const Lane = monaco.editor.OverviewRulerLane.Full;
      const oD = [], mD = [];
      for (const c of changes) {
        const oEnd = c.originalEndLineNumber, mEnd = c.modifiedEndLineNumber;
        if (oEnd > 0) oD.push({ range: new monaco.Range(c.originalStartLineNumber, 1, oEnd, 1),
          options: { overviewRuler: { color: remColor, position: Lane } } });
        if (mEnd > 0) mD.push({ range: new monaco.Range(c.modifiedStartLineNumber, 1, mEnd, 1),
          options: { overviewRuler: { color: insColor, position: Lane } } });
      }
      oDecoIds = oEd.deltaDecorations(oDecoIds, oD);
      mDecoIds = mEd.deltaDecorations(mDecoIds, mD);
    } catch (err) { reportErr('applyRulers', err); }
  }
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
            minimap: { enabled: false },        // 미니맵 제거 — 좌우 스크롤바 대칭·간결
            scrollBeyondLastLine: false,
            renderOverviewRuler: false,         // 통합(우측끝) 룰러 끔 — 좌/우 각 패널 자체 스크롤바가 자기쪽 변경 표시
            renderIndicators: true,             // 거터 +/- 표시(사진처럼)
            renderMarginRevertIcon: false,      // 읽기전용 — revert 아이콘 숨김
            renderLineHighlight: 'none',        // 현재 줄 박스 제거 — 변경 라인만 강조
            diffWordWrap: 'off',
            overviewRulerLanes: 2,
            scrollbar: {
              verticalScrollbarSize: 10, horizontalScrollbarSize: 10,
              verticalSliderSize: 10, horizontalSliderSize: 10,
              vertical: 'auto', horizontal: 'auto', useShadows: false
            }
          });
          editor.onDidUpdateDiff(applyRulers);   // diff 재계산 시 좌/우 룰러 갱신
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
        editor.updateOptions({ renderSideBySide: m.sideBySide !== false });  // 추가 파일 등은 단일 뷰
        editor.setModel({
          original: monaco.editor.createModel(m.originalText || '', lang),
          modified: monaco.editor.createModel(m.modifiedText || '', lang),
        });
      } else if (m.type === 'setTheme') {
        monaco.editor.defineTheme('devez', { base: m.base || 'vs-dark', inherit: true,
          rules: m.rules || [], colors: m.colors || {} });
        monaco.editor.setTheme('devez');
        const c = m.colors || {};
        insColor = c['diffEditorOverviewRuler.insertedForeground'] || c['editorOverviewRuler.addedForeground'] || insColor;
        remColor = c['diffEditorOverviewRuler.removedForeground'] || c['editorOverviewRuler.deletedForeground'] || remColor;
        applyRulers();
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

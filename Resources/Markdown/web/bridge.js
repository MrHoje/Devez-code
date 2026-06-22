(function () {
  'use strict';

  function post(msg) { window.chrome.webview.postMessage(msg); }

  // 정규화 기준선 — setMarkdown(clean) 직후 editor.getMarkdown() 값.
  // Toast UI가 마크다운을 재정렬하므로 원본 파일 텍스트가 아니라 이 기준선과 비교해야
  // 포커스/주입만으로 dirty 오판이 나지 않는다.
  var baseline = '';
  var timer = null;

  var editor = new toastui.Editor({
    el: document.querySelector('#editor'),
    height: '100%',
    initialEditType: 'wysiwyg',
    hideModeSwitch: true,          // 마크다운 탭 숨김 — WYSIWYG 전용
    language: 'ko-KR',
    usageStatistics: false,        // 외부 통신 차단(오프라인 필수)
    autofocus: false,
    plugins: [toastui.Editor.plugin.codeSyntaxHighlight], // 코드블록 신택스 하이라이트(Prism)
    events: {
      change: function () { clearTimeout(timer); timer = setTimeout(emitChange, 150); },
      blur: function () { emitChange(); }
    }
  });

  function currentMd() { return editor.getMarkdown(); }

  // baseline과 비교해 dirty 판정. 내용이 같으면(포커스/주입 에코 등) dirty=false → 오판 방지.
  function emitChange() {
    clearTimeout(timer);
    var md = currentMd();
    post({ type: 'markdownChanged', markdown: md, dirty: md !== baseline });
  }

  // 맞춤법 빨간 밑줄 제거.
  function killSpellcheck() {
    document.querySelectorAll('#editor [contenteditable], #editor textarea')
      .forEach(function (el) { el.setAttribute('spellcheck', 'false'); });
  }
  killSpellcheck();

  // 에디터 내부 Ctrl+S → 즉시 flush 후 저장 요청
  document.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) {
      e.preventDefault();
      emitChange();
      post({ type: 'saveRequested' });
    }
  }, true);

  // Ctrl+휠 → 전체 뷰 배율 조절(0.5~2.5). 실제 줌은 WebView2.ZoomFactor(C#)가 적용한다.
  // (CSS zoom은 축소 시 100% 높이가 뷰포트를 못 채워 빈 공간이 생기므로 사용하지 않음.)
  var zoom = parseFloat(localStorage.getItem('mdZoom') || '1') || 1;
  function applyZoom() { localStorage.setItem('mdZoom', String(zoom)); post({ type: 'zoom', factor: zoom }); }
  applyZoom();
  document.addEventListener('wheel', function (e) {
    if (!e.ctrlKey) return;
    e.preventDefault();
    zoom = Math.min(2.5, Math.max(0.5, zoom + (e.deltaY < 0 ? 0.1 : -0.1)));
    zoom = Math.round(zoom * 10) / 10;
    applyZoom();
  }, { passive: false });

  function setTheme(m) {
    document.body.classList.add('md-themed');
    var ui = document.querySelector('.toastui-editor-defaultUI');
    if (ui) ui.classList.toggle('toastui-editor-dark', !!m.dark);
    var r = document.documentElement.style;
    r.setProperty('--md-bg', m.bg || '#ffffff');
    r.setProperty('--md-text', m.text || '#0f172a');
    r.setProperty('--md-primary', m.primary || '#2563eb');
    r.setProperty('--md-code-bg', m.codeBg || 'rgba(140,140,140,0.18)');
    r.setProperty('--md-code-text', m.codeText || 'inherit');
    document.body.style.background = m.bg || '#ffffff';
  }

  var toastEl = document.getElementById('md-toast');
  var toastTimer = null;
  function showToast(text) {
    toastEl.textContent = text;
    toastEl.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.classList.remove('show'); }, 1400);
  }

  window.chrome.webview.addEventListener('message', function (e) {
    var m = e.data;
    switch (m.type) {
      case 'setMarkdown':
        editor.setMarkdown(m.markdown || '', false);
        killSpellcheck();
        if (m.markClean) {
          baseline = currentMd();                          // 정규화 기준선 갱신
          editor.setScrollTop(0);                          // 파일 전환 시 스크롤 초기화
          // 실제 페인트(레이아웃 완료) 후에 통지 — C# 로딩 스피너가 빈 화면 위에서 먼저 사라지지 않도록.
          // double rAF: 첫 프레임에 레이아웃, 다음 프레임 직전 = 콘텐츠가 화면에 그려진 시점.
          requestAnimationFrame(function () {
            requestAnimationFrame(function () {
              post({ type: 'baseline', markdown: baseline });
            });
          });
        } else {
          emitChange();                                    // AI 주입 등 — dirty 재평가
        }
        break;
      case 'markClean':                                    // 저장 후 — 현재값을 새 기준선으로
        baseline = currentMd();
        break;
      case 'setTheme':
        setTheme(m);
        break;
      case 'setEditable':
        document.body.classList.toggle('md-locked', !m.on);
        break;
      case 'toast':
        showToast(m.text || '');
        break;
      case 'focus':
        editor.focus();
        break;
    }
  });

  post({ type: 'pageReady' });
})();

(function () {
  'use strict';

  function post(msg) { window.chrome.webview.postMessage(msg); }

  // 에디터 표면 클릭/포커스 → 호스트에 통지(분할 시 이 패널을 포커스 패널로). WebView2 는 HwndHost 라
  // WPF PreviewMouseDown 이 경계를 못 넘어오므로 터미널과 동일하게 직접 통지한다.
  document.addEventListener('pointerdown', function () { post({ type: 'interact' }); }, true);

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
    // 검색 하이라이트(<mark>)가 DOM에 박힌 상태에서 getMarkdown()을 호출하면
    // HTML→마크다운 변환 시 <mark> 잔여물이 저장 데이터로 들어간다.
    // 또한 highlightText()로 <mark>를 재삽입하면 Toast UI의 change 이벤트가 재귀를 유발한다.
    // → getMarkdown() 전에 하이라이트를 제거하고, 에디터 편집 중에는 재적용하지 않는다.
    //   사용자가 검색창에 다시 입력하거나 Enter로 재검색 시 복원된다.
    if (searchRanges.length > 0) clearHighlights();
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
    if ((e.ctrlKey || e.metaKey) && (e.key === 'f' || e.key === 'F')) {
      e.preventDefault();
      if (searchEl.classList.contains('open')) {
        searchInput.select();
      } else {
        openSearch();
      }
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
    r.setProperty('--md-panel', m.panel || m.bg || '#ffffff');
    r.setProperty('--md-text', m.text || '#0f172a');
    r.setProperty('--md-primary', m.primary || '#2563eb');
    r.setProperty('--md-code-bg', m.codeBg || 'rgba(140,140,140,0.18)');
    r.setProperty('--md-code-text', m.codeText || 'inherit');
    // 검색창 보더/흐린텍스트 — #editor 밖(#md-search)에 정의돼 .toastui-editor-dark 상속 안 받음
    if (m.dark) {
      r.setProperty('--ob-border', 'rgba(160,160,170,0.18)');
      r.setProperty('--ob-muted', 'rgba(180,180,190,0.65)');
    } else {
      r.setProperty('--ob-border', 'rgba(120,120,120,0.22)');
      r.setProperty('--ob-muted', 'rgba(120,120,128,0.85)');
    }
    document.body.style.background = m.bg || '#ffffff';
  }

  function setViewportWidth(width) {
    width = Math.max(0, Math.round(Number(width) || 0));
    var root = document.documentElement.style;
    root.setProperty('--md-viewport-half-width', (width / 2) + 'px');
    document.body.classList.toggle('md-full-width', width === 0);
  }

  var toastEl = document.getElementById('md-toast');
  var toastTimer = null;
  function showToast(text) {
    toastEl.textContent = text;
    toastEl.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.classList.remove('show'); }, 1400);
  }

  // ── 검색 ──────────────────────────────────────────────────
  var searchEl = document.getElementById('md-search');
  var searchInput = document.getElementById('md-search-input');
  var searchCount = document.getElementById('md-search-count');
  // CSS Custom Highlight API — DOM을 건드리지 않고 Range만으로 형광펜.
  // Toast UI v3(ProseMirror)는 DOM 변화에 반응해 상태를 재설정하므로,
  // <mark>를 직접 박으면 제거된다. Highlight API는 DOM을 수정하지 않아 안전하다.
  var searchRanges = [];    // 현재 검색 결과 Range[]
  var searchIndex = -1;     // 현재 선택된 매치 인덱스

  function getWysiwygRoot() {
    // Toast UI Editor WYSIWYG 모드의 편집 가능 영역
    return document.querySelector('.toastui-editor-ww-container .ProseMirror');
  }

  function clearHighlights() {
    try { CSS.highlights.delete('search-match'); } catch (e) {}
    try { CSS.highlights.delete('search-current'); } catch (e) {}
    searchRanges = [];
    searchIndex = -1;
    searchCount.textContent = '0/0';
  }

  function scrollToCurrentMatch() {
    if (searchIndex < 0 || searchIndex >= searchRanges.length) return;
    var r = searchRanges[searchIndex];
    if (!r) return;
    try { r.startContainer.parentElement?.scrollIntoView({ block: 'center', behavior: 'smooth' }); } catch (e) {}
  }

  function highlightText(query) {
    clearHighlights();
    if (!query) { searchCount.textContent = '0/0'; return; }

    var root = getWysiwygRoot();
    if (!root) return;

    var lower = query.toLowerCase();
    var ranges = [];
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null, false);
    while (walker.nextNode()) {
      var node = walker.currentNode;
      var text = node.textContent;
      var lowerText = text.toLowerCase();
      var idx = 0;
      while ((idx = lowerText.indexOf(lower, idx)) >= 0) {
        var r = document.createRange();
        r.setStart(node, idx);
        r.setEnd(node, idx + query.length);
        ranges.push(r);
        idx += query.length;
      }
    }

    searchRanges = ranges;
    if (ranges.length > 0) {
      try { CSS.highlights.set('search-match', new Highlight(...ranges)); } catch (e) {}
      searchIndex = 0;
      try { CSS.highlights.set('search-current', new Highlight(ranges[0])); } catch (e) {}
      searchCount.textContent = '1/' + ranges.length;
      scrollToCurrentMatch();
    }
  }

  function selectMatch(delta) {
    if (searchRanges.length === 0) {
      // emitChange()가 하이라이트를 제거한 상태. 입력값으로 재검색.
      var q = searchInput.value;
      if (q) highlightText(q);
      if (searchRanges.length === 0) return;
    }
    searchIndex = (searchIndex + delta + searchRanges.length) % searchRanges.length;
    try { CSS.highlights.set('search-current', new Highlight(searchRanges[searchIndex])); } catch (e) {}
    searchCount.textContent = (searchIndex + 1) + '/' + searchRanges.length;
    scrollToCurrentMatch();
  }

  function openSearch() {
    // 기존 하이라이트 정리
    clearHighlights();
    searchEl.classList.add('open');
    searchInput.value = '';
    searchCount.textContent = '0/0';
    setTimeout(function () { searchInput.focus(); }, 0);
  }

  function closeSearch() {
    searchEl.classList.remove('open');
    clearHighlights();
    editor.focus();
  }

  document.getElementById('md-search-close').addEventListener('click', closeSearch);
  document.getElementById('md-search-prev').addEventListener('click', function () { selectMatch(-1); });
  document.getElementById('md-search-next').addEventListener('click', function () { selectMatch(1); });
  searchInput.addEventListener('input', function () {
    highlightText(searchInput.value);
  });
  searchInput.addEventListener('keydown', function (e) {
    if (e.key === 'Enter') {
      e.preventDefault();
      if (e.shiftKey) selectMatch(-1);
      else selectMatch(1);
    } else if (e.key === 'Escape') {
      closeSearch();
    }
  });

  window.chrome.webview.addEventListener('message', function (e) {
    var m = e.data;
    switch (m.type) {
      case 'setMarkdown':
        editor.setMarkdown(m.markdown || '', false);
        killSpellcheck();
        if (m.markClean) {
          baseline = currentMd();                          // 정규화 기준선 갱신
          // 실제 페인트(레이아웃 완료) 후에 통지 — C# 로딩 스피너가 빈 화면 위에서 먼저 사라지지 않도록.
          // double rAF: 첫 프레임에 레이아웃, 다음 프레임 직전 = 콘텐츠가 화면에 그려진 시점.
          requestAnimationFrame(function () {
            requestAnimationFrame(function () {
              editor.setScrollTop(0);                      // 레이아웃 완료 후 스크롤 초기화
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
      case 'setBaseline':                                  // 외부 변경 감지 — 콘텐츠는 두고 baseline만 갱신
        baseline = m.markdown || '';                       // (재평가 시점: 다음 사용자 편집/포커스 이벤트)
        break;
      case 'setTheme':
        setTheme(m);
        break;
      case 'setViewportWidth':
        setViewportWidth(m.width);
        break;
      case 'setEditable':
        document.body.classList.toggle('md-locked', !m.on);
        break;
      case 'toast':
        showToast(m.text || '');
        break;
      case 'focus':
        editor.focus();
        // WYSIWYG 모드: contentEditable focus 시 브라우저가 커서 위치로 스크롤할 수 있으므로
        // 커서를 맨 앞으로 이동 + 스크롤 재설정 — MD 파일 열었을 때 맨 아래 붙는 문제 수정.
        editor.moveCursorToStart();
        editor.setScrollTop(0);
        break;
      case 'toggleSearch':
        if (searchEl.classList.contains('open')) closeSearch();
        else openSearch();
        break;
    }
  });

  post({ type: 'pageReady' });
})();

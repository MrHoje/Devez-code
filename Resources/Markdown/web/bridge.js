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
    if (e.key === 'Escape' && tocEl && tocEl.classList.contains('open')) {
      e.preventDefault();
      closeToc();
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
  function codeLanguage(code) {
    var languageClass = Array.from(code.classList || []).find(function (name) {
      return name.indexOf('language-') === 0;
    });
    return languageClass ? languageClass.substring(9).toLowerCase() : '';
  }

  function codeActionButton(title, svg, action) {
    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'md-code-action';
    button.title = title;
    button.setAttribute('aria-label', title);
    button.setAttribute('contenteditable', 'false');
    button.innerHTML = svg;
    button.addEventListener('pointerdown', function (e) {
      e.preventDefault();
      e.stopPropagation();
    });
    button.addEventListener('click', function (e) {
      e.preventDefault();
      e.stopPropagation();
      action();
    });
    return button;
  }

  function copyCode(text) {
    function copied() { showToast('코드를 복사했습니다.'); }
    function fallbackCopy(value) {
      var area = document.createElement('textarea');
      area.value = value;
      area.style.position = 'fixed';
      area.style.opacity = '0';
      document.body.appendChild(area);
      area.select();
      try { document.execCommand('copy'); copied(); }
      finally { area.remove(); }
    }
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(copied).catch(function () { fallbackCopy(text); });
    } else {
      fallbackCopy(text);
    }
  }

  var codeActions = new Map();

  function positionCodeActions() {
    codeActions.forEach(function (actions, pre) {
      if (!pre.isConnected) {
        actions.remove();
        codeActions.delete(pre);
        return;
      }
      var rect = pre.getBoundingClientRect();
      actions.style.top = (rect.top + 7) + 'px';
      actions.style.right = (window.innerWidth - rect.right + 7) + 'px';
      actions.style.display = rect.bottom > 0 && rect.top < window.innerHeight ? 'flex' : 'none';
    });
  }

  function decorateCodeBlocks() {
    document.querySelectorAll('.toastui-editor-contents pre').forEach(function (pre) {
      if (codeActions.has(pre)) return;
      var code = pre.querySelector(':scope > code');
      if (!code) return;

      var actions = document.createElement('div');
      actions.className = 'md-code-actions';
      actions.setAttribute('contenteditable', 'false');
      if (codeLanguage(code) === 'html') {
        actions.appendChild(codeActionButton('브라우저에서 HTML 실행',
          '<svg viewBox="0 0 24 24"><polygon points="5 3 19 12 5 21 5 3"></polygon></svg>',
          function () { post({ type: 'runHtml', html: code.textContent || '' }); }));
      }
      actions.appendChild(codeActionButton('코드 복사',
        '<svg viewBox="0 0 24 24"><rect x="9" y="9" width="11" height="11" rx="2"></rect><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"></path></svg>',
        function () { copyCode(code.textContent || ''); }));
      document.body.appendChild(actions);
      codeActions.set(pre, actions);
    });
    positionCodeActions();
  }

  var tocToggle = document.getElementById('md-toc-toggle');
  var tocEl = document.getElementById('md-toc');
  var tocFrame = 0;

  function closeToc() {
    tocEl.classList.remove('open');
    tocToggle.classList.remove('open');
    tocToggle.setAttribute('aria-expanded', 'false');
  }

  function rebuildToc() {
    tocFrame = 0;
    var headings = Array.from(document.querySelectorAll(
      '.toastui-editor-ww-container .toastui-editor-contents h1,' +
      '.toastui-editor-ww-container .toastui-editor-contents h2,' +
      '.toastui-editor-ww-container .toastui-editor-contents h3,' +
      '.toastui-editor-ww-container .toastui-editor-contents h4,' +
      '.toastui-editor-ww-container .toastui-editor-contents h5,' +
      '.toastui-editor-ww-container .toastui-editor-contents h6'
    )).filter(function (heading) { return heading.textContent.trim().length > 0; });

    tocEl.replaceChildren();
    tocToggle.classList.toggle('available', headings.length > 0);
    if (headings.length === 0) {
      closeToc();
      return;
    }

    var title = document.createElement('div');
    title.className = 'md-toc-title';
    title.textContent = '목차';
    tocEl.appendChild(title);

    var minLevel = Math.min.apply(null, headings.map(function (heading) {
      return Number(heading.tagName.substring(1));
    }));
    headings.forEach(function (heading) {
      var level = Number(heading.tagName.substring(1));
      var item = document.createElement('button');
      item.type = 'button';
      item.className = 'md-toc-item';
      item.textContent = heading.textContent.trim();
      item.title = item.textContent;
      item.style.paddingLeft = (8 + (level - minLevel) * 14) + 'px';
      item.addEventListener('click', function (e) {
        e.preventDefault();
        e.stopPropagation();
        closeToc();
        heading.scrollIntoView({ behavior: 'smooth', block: 'start' });
      });
      tocEl.appendChild(item);
    });
  }

  function scheduleContentDecorations() {
    decorateCodeBlocks();
    if (!tocFrame) tocFrame = requestAnimationFrame(rebuildToc);
  }

  tocToggle.addEventListener('click', function (e) {
    e.preventDefault();
    e.stopPropagation();
    var open = !tocEl.classList.contains('open');
    closeToc();
    if (open) {
      tocEl.classList.add('open');
      tocToggle.classList.add('open');
      tocToggle.setAttribute('aria-expanded', 'true');
    }
  });
  document.addEventListener('pointerdown', function (e) {
    if (!tocEl.contains(e.target) && !tocToggle.contains(e.target)) closeToc();
  });

  new MutationObserver(scheduleContentDecorations)
    .observe(document.getElementById('editor'), { childList: true, subtree: true, characterData: true });
  document.addEventListener('scroll', positionCodeActions, true);
  window.addEventListener('resize', positionCodeActions);
  scheduleContentDecorations();

  var searchEl = document.getElementById('md-search');
  var searchInput = document.getElementById('md-search-input');
  var searchCount = document.getElementById('md-search-count');
  // CSS Custom Highlight API — DOM을 건드리지 않고 Range만으로 형광펜.
  // Toast UI v3(ProseMirror)는 DOM 변화에 반응해 상태를 재설정하므로,
  // <mark>를 직접 박으면 제거된다. Highlight API는 DOM을 수정하지 않아 안전하다.
  var searchRanges = [];    // 현재 검색 결과 Range[]
  var searchIndex = -1;     // 현재 선택된 매치 인덱스
  var lastQuery = null;     // 직전에 검색을 돌린 질의 (input 중복 발생 가드 — 아래 input 핸들러 주석 참고)

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
    lastQuery = null;
    searchCount.textContent = '0/0';
    setTimeout(function () { searchInput.focus(); }, 0);
  }

  function closeSearch() {
    searchEl.classList.remove('open');
    clearHighlights();
    searchInput.value = '';   // 닫으면 검색어를 비운다 → 다시 열면 빈 상태
    lastQuery = null;
    editor.focus();
  }

  document.getElementById('md-search-close').addEventListener('click', closeSearch);
  document.getElementById('md-search-prev').addEventListener('click', function () { selectMatch(-1); });
  document.getElementById('md-search-next').addEventListener('click', function () { selectMatch(1); });
  // IME 조합 확정(한글 입력 후 첫 Enter)은 값이 그대로인 input 이벤트를 한 번 더 발생시키는데,
  // 그때 highlightText 를 다시 돌리면 방금 Enter 로 옮긴 인덱스가 1로 되돌아가
  // "첫 Enter 가 먹지 않는" 증상이 된다 → 값이 같으면 재검색을 건너뛴다.
  searchInput.addEventListener('input', function () {
    if (searchInput.value === lastQuery) return;
    lastQuery = searchInput.value;
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

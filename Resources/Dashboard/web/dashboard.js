(function () {
  'use strict';

  const $ = id => document.getElementById(id);
  const state = {
    socket: null,
    clientId: null,
    controllerId: null,
    sessions: [],
    selected: null,
    term: null,
    termRoomId: null,
    fit: null,
    fitTimer: 0,
    resizeObserver: null,
    imeCleanup: null,
    agent: '',
    theme: null,
    fontFamily: 'Cascadia Mono',
    fontSize: 16,
    reconnect: 0,
    lastSentGeometry: null,
  };
  let toastTimer = 0;

  function toast(text) {
    const el = $('toast');
    el.textContent = text;
    el.classList.add('on');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove('on'), 2200);
  }

  function send(message) {
    if (state.socket && state.socket.readyState === WebSocket.OPEN) {
      state.socket.send(JSON.stringify(message));
      return true;
    }
    return false;
  }

  function sendInput(data) {
    if (!data) return false;
    if (!hasControl()) {
      toast('먼저 제어권을 가져오세요.');
      return false;
    }
    return send({ type: 'input', roomId: state.selected, data });
  }

  function sendImeProbe(eventName, data, extra) {
    const value = data || '';
    const codePoints = Array.from(value).map(ch => 'U+' + ch.codePointAt(0).toString(16).toUpperCase()).join(',');
    const message = `${eventName} composing=${state.imeComposing ? 1 : 0} active=${document.activeElement?.className || document.activeElement?.tagName || ''} data=${JSON.stringify(value)} cps=${codePoints}${extra ? ' ' + extra : ''}`;
    send({ type: 'imeProbe', roomId: state.selected, message: message.slice(0, 4000) });
    console.debug('[DevezCode LAN IME]', message);
  }

  function fromB64(value) {
    const raw = atob(value || '');
    const out = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
    return out;
  }

  function copyText(value) {
    if (navigator.clipboard && window.isSecureContext)
      return navigator.clipboard.writeText(value).catch(() => copyFallback(value));
    copyFallback(value);
    return Promise.resolve();
  }

  function copyFallback(value) {
    const ta = document.createElement('textarea');
    ta.value = value;
    ta.style.cssText = 'position:fixed;left:-9999px;top:0';
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand('copy'); } catch (e) { }
    ta.remove();
  }

  function hasControl() {
    return !!state.clientId && state.controllerId === state.clientId;
  }

  function connect() {
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(protocol + '//' + location.host + '/ws');
    state.socket = ws;
    ws.onopen = () => {
      state.reconnect = 0;
      $('connection').classList.add('online');
      $('connection').querySelector('span').textContent = '연결됨';
    };
    ws.onmessage = e => {
      try { handle(JSON.parse(e.data)); } catch (err) { console.warn(err); }
    };
    ws.onclose = () => {
      if (state.socket !== ws) return;
      $('connection').classList.remove('online');
      $('connection').querySelector('span').textContent = '연결 끊김';
      setTimeout(connect, Math.min(5000, 800 + state.reconnect++ * 500));
    };
  }

  function handle(message) {
    if (message.type === 'hello') {
      state.clientId = message.clientId;
      state.controllerId = message.controllerId;
      renderControl();
      return;
    }
    if (message.type === 'sessions') {
      state.sessions = message.sessions || [];
      state.controllerId = message.controllerId;
      state.theme = message.terminalTheme;
      state.fontFamily = message.fontFamily || state.fontFamily;
      state.fontSize = message.fontSize || 16;
      applyUiTheme(message.uiTheme, message.terminalTheme, message.appTheme);
      renderSessions();
      renderControl();
      syncSelectedMeta();
      return;
    }
    if (message.type === 'control') {
      state.controllerId = message.controllerId;
      renderControl();
      if (hasControl()) queueFit(true);
      return;
    }
    if (message.type === 'snapshot') {
      if (message.roomId !== state.selected) return;
      const session = sessionById(message.roomId) || { roomId: message.roomId, agent: 'shell' };
      ensureTerminal(session);
      state.term.reset();
      state.term.resize(Math.max(2, message.cols), Math.max(2, message.rows));
      sizeTerminal(message.cols, message.rows);
      state.term.write(fromB64(message.data));
      state.term.scrollToBottom();
      if (hasControl()) queueFit(true);
      return;
    }
    if (message.type === 'output') {
      if (message.roomId === state.selected && state.term) state.term.write(fromB64(message.data));
      return;
    }
    if (message.type === 'size') {
      if (message.roomId === state.selected && state.term) {
        state.term.resize(Math.max(2, message.cols), Math.max(2, message.rows));
        sizeTerminal(message.cols, message.rows);
      }
      return;
    }
    if (message.type === 'error') toast(message.message || '요청을 처리하지 못했습니다.');
  }

  function applyUiTheme(ui, term, appTheme) {
    if (ui) for (const [key, value] of Object.entries(ui))
      document.documentElement.style.setProperty('--' + key.replace(/[A-Z]/g, m => '-' + m.toLowerCase()), value);
    if (term && term.background) document.documentElement.style.setProperty('--term-bg', term.background);
    const meta = document.querySelector('meta[name=theme-color]');
    if (meta && ui) meta.content = ui.bg;
    document.documentElement.style.colorScheme = appTheme === 'dark' ? 'dark' : 'light';
    if (state.term && state.theme) state.term.options.theme = themeFor(state.agent);
  }

  function themeFor(agent) {
    const theme = Object.assign({}, state.theme || {});
    if (agent === 'gajae') {
      const bg = theme.background && theme.background.toLowerCase();
      theme.selectionBackground = bg === '#f2ede6' ? '#C2D8B0' : bg === '#f8fafc' ? '#C5D8F8' : '#264F78';
    }
    return theme;
  }

  function sessionById(id) { return state.sessions.find(session => session.roomId === id); }

  function renderSessions() {
    $('session-count').textContent = state.sessions.length;
    const list = $('session-list');
    list.textContent = '';
    if (!state.sessions.length) {
      const empty = document.createElement('div');
      empty.className = 'empty-state';
      empty.style.position = 'static';
      empty.style.height = '180px';
      empty.innerHTML = '<strong>실행 중인 세션이 없습니다</strong><span>DevezCode에서 세션을 열면 표시됩니다.</span>';
      list.appendChild(empty);
      return;
    }
    for (const session of state.sessions) {
      const button = document.createElement('button');
      button.className = 'session-card' + (session.roomId === state.selected ? ' active' : '');
      button.innerHTML = '<i class="live-dot"></i><span class="session-copy"><strong></strong><span></span></span><em class="agent-mini"></em>';
      button.querySelector('strong').textContent = session.name;
      button.querySelector('.session-copy span').textContent = session.projectName || 'DevezCode';
      button.querySelector('em').textContent = session.agent;
      button.onclick = () => selectSession(session.roomId);
      list.appendChild(button);
    }
  }

  function selectSession(roomId) {
    state.selected = roomId;
    state.lastSentGeometry = null;
    renderSessions();
    syncSelectedMeta();
    send({ type: 'subscribe', roomId });
    closeSidebar();
  }

  function syncSelectedMeta() {
    const session = sessionById(state.selected);
    if (!session) {
      if (state.selected) { state.selected = null; disposeTerminal(); }
      $('session-header').classList.add('hidden');
      $('empty').classList.remove('hidden');
      return;
    }
    $('session-header').classList.remove('hidden');
    $('empty').classList.add('hidden');
    $('session-title').textContent = session.name;
    $('session-project').textContent = session.projectName;
    $('agent-pill').textContent = session.agent;
    $('size-label').textContent = session.cols + ' × ' + session.rows;
    if (state.term && (state.term.cols !== session.cols || state.term.rows !== session.rows)) {
      state.term.resize(Math.max(2, session.cols), Math.max(2, session.rows));
      sizeTerminal(session.cols, session.rows);
    }
  }

  function installFit(term, session) {
    const namespace = session.agent === 'codex' && window.FitAddon6 ? window.FitAddon6 : window.FitAddon;
    if (!namespace || !namespace.FitAddon) return null;
    const fit = new namespace.FitAddon();
    term.loadAddon(fit);
    return fit;
  }

  function installIme(term, roomId) {
    const root = $('terminal');
    const textarea = root.querySelector('.xterm-helper-textarea');
    if (!textarea) {
      sendImeProbe('helper-missing', '', 'room=' + roomId);
      return () => { };
    }
    textarea.setAttribute('autocomplete', 'off');
    textarea.setAttribute('spellcheck', 'false');
    let composing = false;
    let lastDataAt = -Infinity;
    let endToken = 0;
    state.imeComposing = false;

    const disposables = [];
    const listen = (event, handler) => {
      textarea.addEventListener(event, handler);
      disposables.push(() => textarea.removeEventListener(event, handler));
    };
    listen('compositionstart', () => {
      composing = true;
      state.imeComposing = true;
      sendImeProbe('compositionstart');
    });
    listen('compositionupdate', event => {
      sendImeProbe('compositionupdate', event.data || '');
    });
    listen('compositionend', event => {
      composing = false;
      state.imeComposing = false;
      const committed = event.data || '';
      const endedAt = performance.now();
      const token = ++endToken;
      sendImeProbe('compositionend', committed);
      // xterm normally emits onData after compositionend. The fallback only fires when
      // that event did not reach us, preventing lost Hangul while avoiding duplicate input.
      if (committed) setTimeout(() => {
        if (token === endToken && lastDataAt < endedAt) {
          sendInput(committed);
          sendImeProbe('composition-fallback', committed);
        }
      }, 60);
    });
    listen('beforeinput', event => {
      if (event.isComposing || (event.inputType || '').includes('Composition'))
        sendImeProbe('beforeinput', event.data || '', 'type=' + (event.inputType || ''));
    });
    listen('input', event => {
      if (event.isComposing) sendImeProbe('input', event.data || '');
    });
    listen('keydown', event => {
      if (event.isComposing || event.keyCode === 229) sendImeProbe('keydown', '', 'key=' + event.key + ' code=' + event.keyCode);
    });
    listen('blur', () => {
      if (composing) sendImeProbe('blur-during-composition');
    });

    const inputDisposable = term.onData(data => {
      lastDataAt = performance.now();
      sendInput(data);
    });
    disposables.push(() => inputDisposable.dispose());
    sendImeProbe('ime-ready', '', 'textarea=' + textarea.className);
    return () => {
      state.imeComposing = false;
      for (const dispose of disposables) dispose();
    };
  }

  function ensureTerminal(session) {
    if (state.term && state.termRoomId === session.roomId && state.agent === session.agent) return;
    disposeTerminal();
    state.agent = session.agent;
    state.termRoomId = session.roomId;
    const use6 = session.agent === 'codex' && window.Terminal6;
    const Ctor = use6 ? window.Terminal6 : window.Terminal;
    const term = new Ctor({
      theme: themeFor(session.agent),
      fontFamily: state.fontFamily + ", Cascadia Mono, Consolas, 'D2Coding', 'NanumGothicCoding', 'Malgun Gothic', monospace",
      fontSize: state.fontSize,
      cursorBlink: true,
      allowProposedApi: true,
      scrollback: 5000,
      windowsPty: { backend: 'conpty', buildNumber: 0 },
    });
    state.term = term;
    $('terminal').classList.add('ready');
    term.open($('terminal'));
    state.fit = installFit(term, session);
    state.imeCleanup = installIme(term, session.roomId);
    term.attachCustomKeyEventHandler(event => {
      if (event.isComposing) return true;
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'c' && term.hasSelection()) {
        copyText(term.getSelection());
        term.clearSelection();
        return false;
      }
      return true;
    });
    setTimeout(() => term.focus(), 0);
  }

  function queueFit(sendResize) {
    clearTimeout(state.fitTimer);
    state.fitTimer = setTimeout(() => fitToViewport(sendResize), 40);
  }

  function fitToViewport(sendResize) {
    if (!state.term || !state.fit) return;
    const host = $('terminal-scroll');
    if (host.clientWidth < 80 || host.clientHeight < 60) return;
    try { state.fit.fit(); } catch (error) { console.warn('LAN terminal fit failed', error); return; }
    const cols = state.term.cols;
    const rows = state.term.rows;
    sizeTerminal(cols, rows);
    if (!sendResize || !hasControl() || !state.selected) return;
    const geometry = cols + 'x' + rows;
    if (geometry === state.lastSentGeometry) return;
    state.lastSentGeometry = geometry;
    send({ type: 'resize', roomId: state.selected, cols, rows });
  }

  function sizeTerminal(cols, rows) {
    $('size-label').textContent = cols + ' × ' + rows;
  }

  function disposeTerminal() {
    clearTimeout(state.fitTimer);
    if (state.imeCleanup) { state.imeCleanup(); state.imeCleanup = null; }
    if (state.fit) { try { state.fit.dispose(); } catch (e) { } state.fit = null; }
    if (state.term) { try { state.term.dispose(); } catch (e) { } state.term = null; }
    state.termRoomId = null;
    state.agent = '';
    state.imeComposing = false;
    $('terminal').textContent = '';
    $('terminal').classList.remove('ready');
  }

  function renderControl() {
    const active = hasControl();
    const button = $('control');
    button.classList.toggle('active', active);
    button.textContent = active ? '제어 중' : '제어권 가져오기';
  }

  function openSidebar() { $('sidebar').classList.add('open'); $('scrim').classList.add('on'); }
  function closeSidebar() { $('sidebar').classList.remove('open'); $('scrim').classList.remove('on'); }

  $('control').onclick = () => {
    if (!hasControl()) send({ type: 'claimControl' });
    else { state.term && state.term.focus(); queueFit(true); }
  };
  $('refresh').onclick = () => send({ type: 'refresh' });
  $('sidebar-toggle').onclick = openSidebar;
  $('scrim').onclick = closeSidebar;
  $('mobile-keys').addEventListener('click', event => {
    const button = event.target.closest('button[data-code]');
    if (!button) return;
    const keys = { esc: '\x1b', ctrlc: '\x03', up: '\x1b[A', down: '\x1b[B', left: '\x1b[D', right: '\x1b[C', enter: '\r' };
    if (sendInput(keys[button.dataset.code] || '')) state.term && state.term.focus();
  });

  if (window.ResizeObserver) {
    state.resizeObserver = new ResizeObserver(() => {
      if (hasControl()) queueFit(true);
    });
    state.resizeObserver.observe($('terminal-scroll'));
  }
  window.addEventListener('resize', () => { if (hasControl()) queueFit(true); });
  connect();
})();

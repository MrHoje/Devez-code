(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  root.DevezQuestionCorners = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  function rgb(value) {
    return '#' + (value & 0xffffff).toString(16).padStart(6, '0');
  }

  // DevezVibe의 얇은 반 줄 모서리만 찾는다. 일반 문자·다른 색·단독 사분면은 건드리지 않는다.
  function findCorners(term) {
    const result = [];
    const buffer = term.buffer.active;
    for (let row = 0; row < term.rows; row++) {
      const lineIndex = buffer.viewportY + row;
      const line = buffer.getLine(lineIndex);
      if (!line || !/[▖▘]/.test(line.translateToString())) continue;
      for (let col = 1; col + 1 < term.cols; col++) {
        const cell = line.getCell(col);
        const glyph = cell && cell.getChars();
        if (glyph !== '▖' && glyph !== '▘') continue;
        if (!cell.isFgRGB() || !cell.isBgRGB() || cell.isInverse()) continue;
        const top = glyph === '▖';
        const right = line.getCell(col + 1);
        const bodyLine = buffer.getLine(lineIndex + (top ? 1 : -1));
        const body = bodyLine && bodyLine.getCell(col);
        if (!right || right.getChars() !== (top ? '▄' : '▀') || right.isInverse() || !right.isFgRGB() ||
            right.getFgColor() !== cell.getBgColor() || !body || body.getChars() !== '▌' ||
            body.isInverse() || !body.isFgRGB() || body.getFgColor() !== cell.getFgColor() ||
            !body.isBgRGB() || body.getBgColor() !== cell.getBgColor()) continue;
        const left = line.getCell(col - 1);
        result.push({
          row: row, col: col, top: top,
          line: rgb(cell.getFgColor()), fill: rgb(cell.getBgColor()),
          outside: left && left.isBgRGB() ? rgb(left.getBgColor()) : term.options.theme.background
        });
        // 보정은 화면에 보이는 모서리만 소유한다. 비정상 출력도 DOM을 무한히 늘리지 못한다.
        if (result.length === 512) return result;
      }
    }
    return result;
  }

  function install(term, agent) {
    if (agent !== 'devezvibe') return false;
    const screen = term.element && term.element.querySelector('.xterm-screen');
    if (!screen) return false;
    const doc = screen.ownerDocument;
    const win = doc.defaultView;
    const layer = doc.createElement('div');
    layer.className = 'devez-question-corners';
    layer.setAttribute('aria-hidden', 'true');
    Object.assign(layer.style, {
      position: 'absolute', inset: '0', overflow: 'hidden', pointerEvents: 'none', zIndex: '5'
    });
    screen.appendChild(layer);
    const cells = new Map();
    const subscriptions = [];
    let disposed = false;
    let pending = 0;

    function render() {
      pending = 0;
      if (disposed) return;
      if (term.modes && term.modes.synchronizedOutputMode) return;
      const rect = screen.getBoundingClientRect();
      const corners = rect.width > 0 && rect.height > 0 && !term.hasSelection() ? findCorners(term) : [];
      const keep = new Set();
      for (const corner of corners) {
        const key = corner.row + ':' + corner.col;
        keep.add(key);
        let el = cells.get(key);
        if (!el) {
          el = doc.createElement('div');
          el.style.position = 'absolute';
          layer.appendChild(el);
          cells.set(key, el);
        }
        // 한 글자 칸을 브라우저 픽셀로 덮어 선·상자·바깥 배경의 세 색을 함께 그린다.
        Object.assign(el.style, {
          left: (corner.col * rect.width / term.cols) + 'px',
          top: (corner.row * rect.height / term.rows) + 'px',
          width: (rect.width / term.cols) + 'px',
          height: (rect.height / term.rows) + 'px',
          backgroundColor: corner.outside,
          backgroundImage: 'linear-gradient(to right,' + corner.line + ' 0%,' + corner.line + ' 50%,' + corner.fill + ' 50%,' + corner.fill + ' 100%)',
          backgroundSize: '100% 50%', backgroundRepeat: 'no-repeat',
          backgroundPosition: corner.top ? 'left bottom' : 'left top'
        });
      }
      for (const [key, el] of cells) {
        if (!keep.has(key)) { el.remove(); cells.delete(key); }
      }
    }

    function schedule() {
      if (!disposed && !pending) pending = win.requestAnimationFrame(render);
    }

    term.loadAddon({
      activate: function () {
        subscriptions.push(term.onRender(schedule), term.onScroll(schedule),
          term.onResize(schedule), term.onSelectionChange(schedule));
        schedule();
      },
      dispose: function () {
        disposed = true;
        if (pending) win.cancelAnimationFrame(pending);
        for (const subscription of subscriptions) subscription.dispose();
        cells.clear();
        layer.remove();
      }
    });
    return true;
  }

  return { install: install, findCorners: findCorners };
});

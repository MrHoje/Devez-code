(() => {
  'use strict';

  const scrollHost = document.getElementById('terminal-scroll');
  if (!scrollHost) return;

  const isMobile = () => matchMedia('(max-width:720px), (max-height:520px) and (pointer:coarse)').matches;
  let touch = null;

  function focusTerminalInput() {
    const input = scrollHost.querySelector('.xterm-helper-textarea');
    if (!input) return;
    try { input.focus({ preventScroll: true }); }
    catch { input.focus(); }
  }

  scrollHost.addEventListener('touchstart', event => {
    if (!isMobile() || event.touches.length !== 1) return;
    const point = event.touches[0];
    touch = { x: point.clientX, y: point.clientY, lastY: point.clientY, moved: false };
  }, { passive: true });

  scrollHost.addEventListener('touchmove', event => {
    if (!touch || !isMobile() || event.touches.length !== 1) return;
    const point = event.touches[0];
    const distanceX = point.clientX - touch.x;
    const distanceY = point.clientY - touch.y;
    const deltaY = touch.lastY - point.clientY;
    touch.lastY = point.clientY;

    if (Math.abs(distanceX) > 8 || Math.abs(distanceY) > 8) touch.moved = true;
    if (!touch.moved || Math.abs(deltaY) < 1) return;

    // xterm의 실제 스크롤백은 변환된 외부 컨테이너가 아니라 viewport에 있다.
    // 터치 이동을 viewport 스크롤로 직접 옮겨 모바일에서도 과거 출력으로 이동한다.
    const viewport = scrollHost.querySelector('.xterm-viewport');
    if (viewport) viewport.scrollTop += deltaY;
    event.preventDefault();
  }, { passive: false });

  scrollHost.addEventListener('touchend', () => {
    if (touch && !touch.moved) focusTerminalInput();
    touch = null;
  }, { passive: true });
  scrollHost.addEventListener('touchcancel', () => { touch = null; }, { passive: true });

  // 터미널을 탭한 바로 그 사용자 제스처 안에서 포커스해야 iOS/Android가 가상 키보드를 연다.
  scrollHost.addEventListener('click', () => {
    if (isMobile()) focusTerminalInput();
  });
})();

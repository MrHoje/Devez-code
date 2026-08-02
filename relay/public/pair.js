(function () {
  'use strict';
  const card = document.getElementById('card');
  const params = new URLSearchParams(location.search);
  const initialCode = (params.get('code') || '').toUpperCase();
  // 앱이 딥링크로 코드를 전달하면 코드 입력을 숨기고 승인 버튼만 노출한다.
  const hasCode = /^[A-Z0-9]{4,}$/.test(initialCode);

  function renderForm(email) {
    card.innerHTML =
      '<h1>PC 연결 승인</h1>' +
      '<p><b>' + escapeHtml(email) + '</b> 계정에 이 PC를 연결합니다.</p>' +
      (hasCode ? '' :
        '<input id="code" maxlength="8" placeholder="앱에 표시된 코드 입력" ' +
          'style="width:100%;height:42px;margin-bottom:10px;border:1px solid var(--line);border-radius:8px;background:var(--panel-soft);color:var(--text);font-size:15px;letter-spacing:.08em;text-align:center;text-transform:uppercase">') +
      '<input id="name" placeholder="PC 이름(선택)" ' +
        'style="width:100%;height:38px;margin-bottom:14px;border:1px solid var(--line);border-radius:8px;background:var(--panel-soft);color:var(--text);font-size:13px;padding:0 10px">' +
      '<button id="approve" class="login-button">' + (hasCode ? '이 PC 연결 승인' : '승인') + '</button>' +
      '<div id="msg" style="margin-top:10px;color:var(--muted);font-size:12px"></div>';

    document.getElementById('approve').onclick = approve;
  }

  function approve() {
    const codeEl = document.getElementById('code');
    const userCode = (codeEl ? codeEl.value : initialCode).trim().toUpperCase();
    const name = document.getElementById('name').value.trim();
    const msg = document.getElementById('msg');
    if (!userCode) { msg.textContent = '코드가 없습니다. 앱에서 다시 페어링해 주세요.'; return; }
    document.getElementById('approve').disabled = true;
    msg.textContent = '승인 중…';
    fetch('/api/pair/approve', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userCode, name }),
    })
      .then(res => res.json().then(body => ({ ok: res.ok, body })))
      .then(({ ok, body }) => {
        msg.textContent = ok ? 'PC가 연결되었습니다. 앱에서 자동으로 완료됩니다 — 이 창은 잠시 후 닫힙니다. 닫지 말고 기다려 주세요.' : (body.error || '승인에 실패했습니다.');
        if (!ok) document.getElementById('approve').disabled = false;
      })
      .catch(() => { msg.textContent = '네트워크 오류로 승인에 실패했습니다.'; document.getElementById('approve').disabled = false; });
  }

  function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  fetch('/api/session')
    .then(res => res.json())
    .then(data => {
      if (!data.loggedIn) {
        location.href = '/auth/login?next=' + encodeURIComponent(location.pathname + location.search);
        return;
      }
      renderForm(data.email);
    });
})();

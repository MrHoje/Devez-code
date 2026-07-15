(function () {
  'use strict';
  const card = document.getElementById('card');

  function renderLogin() {
    card.innerHTML =
      '<h1>로그인</h1>' +
      '<p>등록된 Google 계정으로 로그인하면 소유한 DevezCode PC 목록이 표시됩니다.</p>' +
      '<a class="login-button" href="/auth/login">Google로 로그인</a>';
  }

  function renderDevices(email, devices) {
    const rows = devices.length
      ? devices.map(d =>
          '<div class="device-item">' +
            '<a class="device-row" href="/app/' + encodeURIComponent(d.deviceId) + '">' +
              '<span><span class="device-name">' + escapeHtml(d.name || '이름 없는 PC') + '</span>' +
              '<div class="device-sub">' + new Date(d.createdAt).toLocaleDateString() + '</div></span>' +
              '<span class="dot' + (d.online ? ' online' : '') + '"></span>' +
            '</a>' +
            '<button class="device-del" data-id="' + escapeHtml(d.deviceId) + '" data-name="' + escapeHtml(d.name || '이름 없는 PC') + '" title="이 PC 삭제">삭제</button>' +
          '</div>'
        ).join('')
      : '<div class="empty">등록된 PC가 없습니다. DevezCode 설정에서 외부 접속을 켜고 페어링하세요.</div>';

    card.innerHTML =
      '<div class="head-row"><h1 style="margin:0">내 PC</h1><span>' + escapeHtml(email) + '</span></div>' +
      '<div class="device-list">' + rows + '</div>' +
      '<div style="margin-top:16px;display:flex;justify-content:space-between">' +
        '<a class="text-link" href="/pair">새 PC 페어링</a>' +
        '<a class="text-link" href="/auth/logout">로그아웃</a>' +
      '</div>';

    card.querySelectorAll('.device-del').forEach(btn => {
      btn.onclick = () => deleteDevice(btn.getAttribute('data-id'), btn.getAttribute('data-name'));
    });
  }

  function deleteDevice(deviceId, name) {
    if (!confirm('"' + name + '" 을(를) 삭제할까요?\n삭제하면 이 PC의 원격 접속이 끊기고, 다시 쓰려면 재페어링해야 합니다.')) return;
    fetch('/api/devices/' + encodeURIComponent(deviceId), { method: 'DELETE' })
      .then(res => res.json().then(body => ({ ok: res.ok, body })))
      .then(({ ok, body }) => {
        if (!ok) { alert(body.error || '삭제에 실패했습니다.'); return; }
        loadDevices();
      })
      .catch(() => alert('네트워크 오류로 삭제에 실패했습니다.'));
  }

  function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function loadDevices() {
    fetch('/api/devices')
      .then(res => {
        if (res.status === 401) { renderLogin(); return null; }
        return res.json();
      })
      .then(data => { if (data) renderDevices(data.email, data.devices || []); })
      .catch(() => renderLogin());
  }

  loadDevices();
})();

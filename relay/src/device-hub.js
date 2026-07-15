// Durable Object: deviceId 당 1 인스턴스. PC 아웃바운드 WS 1개 + 브라우저 WS N개를 보관하고
// clientId 봉투(스펙 4.1)로 멀티플렉스한다. WebSocket Hibernation API 사용 — 상태는 in-memory
// 맵이 아니라 ws.serializeAttachment/ctx.getWebSockets(tag) 로 유지해야 hibernate 후에도 살아남는다.

export class DeviceHub {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
  }

  async fetch(request) {
    const url = new URL(request.url);

    if (url.pathname === '/status') {
      return Response.json({ online: this.ctx.getWebSockets('pc').length > 0 });
    }

    if (url.pathname === '/revoke') {
      // 디바이스 삭제 시 활성 PC/브라우저 소켓을 즉시 닫는다.
      for (const ws of this.ctx.getWebSockets()) {
        try { ws.close(1000, 'device removed'); } catch { /* 무시 */ }
      }
      return Response.json({ ok: true });
    }

    if (request.headers.get('Upgrade') !== 'websocket') {
      return new Response('expected websocket', { status: 400 });
    }

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);

    if (url.pathname === '/device') {
      this.ctx.acceptWebSocket(server, ['pc']);
      server.serializeAttachment({ role: 'pc' });
      // PC 재연결: 이미 붙어있는 브라우저들을 새 PC 세션에 다시 알려 스냅샷을 재구동한다.
      for (const browser of this.ctx.getWebSockets('browser')) {
        const att = browser.deserializeAttachment() || {};
        if (att.clientId) {
          try { server.send(JSON.stringify({ clientId: att.clientId, kind: 'join' })); } catch { /* 무시 */ }
        }
      }
    } else if (url.pathname === '/client') {
      const clientId = crypto.randomUUID();
      const email = request.headers.get('X-Owner-Email') || '';
      this.ctx.acceptWebSocket(server, ['browser', `client:${clientId}`]);
      server.serializeAttachment({ role: 'browser', clientId, email });
      if (this.ctx.getWebSockets('pc').length > 0) {
        this.sendToPc({ clientId, kind: 'join' });
      } else {
        server.send(JSON.stringify({ type: 'offline' }));
      }
    } else {
      return new Response('not found', { status: 404 });
    }

    return new Response(null, { status: 101, webSocket: client });
  }

  sendToPc(envelope) {
    const text = JSON.stringify(envelope);
    for (const ws of this.ctx.getWebSockets('pc')) {
      try { ws.send(text); } catch { /* 소켓이 닫히는 중이면 무시 */ }
    }
    return this.ctx.getWebSockets('pc').length > 0;
  }

  async webSocketMessage(ws, message) {
    const att = ws.deserializeAttachment() || {};

    if (att.role === 'pc') {
      let envelope;
      try { envelope = JSON.parse(message); } catch { console.warn('[device-hub] malformed envelope from pc'); return; }
      if (envelope.kind !== 'msg') return;
      const targets = envelope.clientId === '*'
        ? this.ctx.getWebSockets('browser')
        : this.ctx.getWebSockets(`client:${envelope.clientId}`);
      const text = JSON.stringify(envelope.payload);
      for (const target of targets) {
        try { target.send(text); } catch { /* 무시 */ }
      }
      return;
    }

    if (att.role === 'browser') {
      let payload;
      try { payload = JSON.parse(message); } catch { console.warn('[device-hub] malformed message from browser'); return; }
      // 감사 로그: 누가 어느 room 에 input 했는지 추적(스펙 7절 RCE 추적).
      if (payload?.type === 'input') {
        console.log(`[audit] input by=${att.email} room=${payload.roomId} bytes=${(payload.data || '').length}`);
      }
      // 브라우저 연결 직후 refresh는 join 유실 복구용 핸드셰이크이기도 하다.
      // 구버전 PC 커넥터도 join을 먼저 받으면 즉시 전체 세션 스냅샷을 반환한다.
      if (payload?.type === 'refresh') {
        this.sendToPc({ clientId: att.clientId, kind: 'join' });
      }
      const delivered = this.sendToPc({ clientId: att.clientId, kind: 'msg', payload });
      if (!delivered) ws.send(JSON.stringify({ type: 'offline' }));
      return;
    }
  }

  async webSocketClose(ws) {
    const att = ws.deserializeAttachment() || {};
    if (att.role === 'browser') {
      this.sendToPc({ clientId: att.clientId, kind: 'leave' });
    } else if (att.role === 'pc') {
      const offline = JSON.stringify({ type: 'offline' });
      for (const browser of this.ctx.getWebSockets('browser')) {
        try { browser.send(offline); } catch { /* 무시 */ }
      }
    }
  }

  async webSocketError(ws) {
    await this.webSocketClose(ws);
  }
}

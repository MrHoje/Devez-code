// WS 업그레이드 라우팅: 인증 게이트(스펙 4.3, RCE 방어 최우선) 통과 후 DeviceHub DO 로 전달.

import { requireSession } from '../auth/session.js';
import { getDevice } from '../kv.js';
import { sha256Hex, timingSafeEqual } from '../util.js';

export async function routeClientWs(request, env, deviceId) {
  if (request.headers.get('Upgrade') !== 'websocket') return new Response('expected websocket', { status: 400 });

  const session = await requireSession(request, env);
  if (!session) return new Response('unauthorized', { status: 403 });

  const device = await getDevice(env, deviceId);
  if (!device || device.ownerSub !== session.ownerSub) {
    // 핵심 RCE 게이트: 소유자 불일치는 무조건 403, 예외 없음.
    console.warn(`[audit] denied client ws: device=${deviceId} by=${session.email}`);
    return new Response('forbidden', { status: 403 });
  }

  const newUrl = new URL(request.url);
  newUrl.pathname = '/client';
  const forward = new Request(newUrl, request);
  forward.headers.set('X-Owner-Email', session.email);
  console.log(`[audit] client ws connect: device=${deviceId} by=${session.email}`);

  const stub = env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(deviceId));
  return stub.fetch(forward);
}

export async function routeDeviceWs(request, env, deviceId) {
  if (request.headers.get('Upgrade') !== 'websocket') return new Response('expected websocket', { status: 400 });

  const device = await getDevice(env, deviceId);
  const secret = request.headers.get('X-Device-Secret') || '';
  const secretHash = secret ? await sha256Hex(secret) : '';
  if (!device || !secret || !timingSafeEqual(secretHash, device.secretHash)) {
    return new Response('unauthorized', { status: 401 });
  }

  const newUrl = new URL(request.url);
  newUrl.pathname = '/device';
  const forward = new Request(newUrl, request);
  console.log(`[audit] device ws connect: device=${deviceId}`);

  const stub = env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(deviceId));
  return stub.fetch(forward);
}

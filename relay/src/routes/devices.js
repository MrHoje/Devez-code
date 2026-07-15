// /api/devices, /api/session : 로그인 사용자 소유 디바이스 목록 + 온라인 상태.

import { requireSession } from '../auth/session.js';
import { getOwnerDeviceIds, getDevice, removeDevice, removeOwnerDevice } from '../kv.js';

export async function handleSessionApi(request, env) {
  const session = await requireSession(request, env);
  if (!session) return Response.json({ loggedIn: false });
  return Response.json({ loggedIn: true, email: session.email });
}

export async function handleDevicesApi(request, env) {
  const session = await requireSession(request, env);
  if (!session) return Response.json({ error: 'unauthorized' }, { status: 401 });

  const deviceIds = await getOwnerDeviceIds(env, session.ownerSub);
  const devices = [];
  for (const deviceId of deviceIds) {
    const device = await getDevice(env, deviceId);
    if (!device) continue;
    devices.push({ deviceId, name: device.name, createdAt: device.createdAt, online: await isDeviceOnline(env, deviceId) });
  }
  return Response.json({ email: session.email, devices });
}

export async function handleDeleteDevice(request, env, deviceId) {
  const session = await requireSession(request, env);
  if (!session) return Response.json({ error: 'unauthorized' }, { status: 401 });

  const device = await getDevice(env, deviceId);
  // 소유자 검증: 남의 디바이스는 삭제 불가(존재해도 not_found 로 응답해 정보 노출 방지).
  if (!device || device.ownerSub !== session.ownerSub) {
    return Response.json({ error: 'not_found' }, { status: 404 });
  }

  await removeDevice(env, deviceId);
  await removeOwnerDevice(env, session.ownerSub, deviceId);
  // 활성 연결 즉시 해제(best-effort). 실패해도 KV 삭제로 재연결은 차단됨.
  try {
    const stub = env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(deviceId));
    await stub.fetch('https://internal/revoke', { method: 'POST' });
  } catch { /* 무시 */ }

  console.log(`[audit] device deleted: device=${deviceId} by=${session.email}`);
  return Response.json({ ok: true });
}

async function isDeviceOnline(env, deviceId) {
  try {
    const stub = env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(deviceId));
    const res = await stub.fetch('https://internal/status');
    const json = await res.json();
    return json.online === true;
  } catch {
    return false;
  }
}

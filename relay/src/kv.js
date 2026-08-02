// KV 스키마 접근 헬퍼. 스펙 5절: device/owner/pair/allowlist.

const ALLOWLIST_KEY = 'allowlist';

const deviceKey = deviceId => `device:${deviceId}`;
const ownerKey = ownerSub => `owner:${ownerSub}`;
const pairKey = userCode => `pair:${userCode}`;

export async function getDevice(env, deviceId) {
  const raw = await env.DEVICES.get(deviceKey(deviceId));
  return raw ? JSON.parse(raw) : null;
}

export async function putDevice(env, deviceId, device) {
  await env.DEVICES.put(deviceKey(deviceId), JSON.stringify(device));
}

export async function getOwnerDeviceIds(env, ownerSub) {
  const raw = await env.DEVICES.get(ownerKey(ownerSub));
  return raw ? JSON.parse(raw) : [];
}

export async function addOwnerDevice(env, ownerSub, deviceId) {
  const ids = await getOwnerDeviceIds(env, ownerSub);
  if (!ids.includes(deviceId)) {
    ids.push(deviceId);
    await env.DEVICES.put(ownerKey(ownerSub), JSON.stringify(ids));
  }
}

export async function removeDevice(env, deviceId) {
  await env.DEVICES.delete(deviceKey(deviceId));
}

export async function removeOwnerDevice(env, ownerSub, deviceId) {
  const ids = await getOwnerDeviceIds(env, ownerSub);
  const next = ids.filter(id => id !== deviceId);
  if (next.length !== ids.length) await env.DEVICES.put(ownerKey(ownerSub), JSON.stringify(next));
}

export async function getPair(env, userCode) {
  const raw = await env.DEVICES.get(pairKey(userCode));
  return raw ? JSON.parse(raw) : null;
}

export async function putPair(env, userCode, record, ttlSeconds) {
  await env.DEVICES.put(pairKey(userCode), JSON.stringify(record), ttlSeconds ? { expirationTtl: ttlSeconds } : undefined);
}

export async function isAllowedEmail(env, email) {
  // 개방(셀프서비스) 모드: allowlist 미설정/빈 배열이면 검증된 이메일은 모두 허용.
  // 나중에 allowlist 에 항목을 넣으면 그 목록으로 제한된다.
  const raw = await env.DEVICES.get(ALLOWLIST_KEY);
  if (!raw) return true;
  const list = JSON.parse(raw);
  if (!Array.isArray(list) || list.length === 0) return true;
  const target = (email || '').toLowerCase();
  return list.some(e => e.toLowerCase() === target);
}

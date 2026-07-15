// OAuth device-flow 유사 페어링(스펙 4.2). start(앱, 무인증) -> approve(브라우저, 로그인 필요) -> poll(앱, 무인증).

import { requireSession } from '../auth/session.js';
import { getPair, putPair, putDevice, addOwnerDevice } from '../kv.js';
import { randomUserCode, randomToken, sha256Hex } from '../util.js';

const PAIR_TTL_SECONDS = 600; // 페어링 진행 유효 시간 10분
const SECRET_HANDOFF_TTL_SECONDS = 120; // 승인 후 앱이 secret을 수령할 유예 시간

async function readJson(request) {
  try {
    return await request.json();
  } catch {
    return null;
  }
}

export async function handlePairStart(request, env) {
  const userCode = randomUserCode(8);
  await putPair(env, userCode, { status: 'pending' }, PAIR_TTL_SECONDS);
  const url = new URL(request.url);
  return Response.json({
    userCode,
    verificationUrl: `${url.protocol}//${url.host}/pair?code=${userCode}`,
    expiresIn: PAIR_TTL_SECONDS,
  });
}

export async function handlePairPoll(request, env) {
  const body = await readJson(request);
  const userCode = body?.userCode;
  if (!userCode) return Response.json({ error: 'userCode required' }, { status: 400 });

  const pair = await getPair(env, userCode);
  if (!pair) return Response.json({ status: 'expired' });
  if (pair.status === 'approved') {
    // deviceSecret 평문은 1회만 지급하고 즉시 KV에서 제거(평문 미보관 원칙, 스펙 7절).
    await putPair(env, userCode, { status: 'completed' }, 30);
    return Response.json({ status: 'approved', deviceId: pair.deviceId, deviceSecret: pair.deviceSecret });
  }
  return Response.json({ status: pair.status });
}

export async function handlePairApprove(request, env) {
  const session = await requireSession(request, env);
  if (!session) return Response.json({ error: 'unauthorized' }, { status: 401 });

  const body = await readJson(request);
  const userCode = body?.userCode;
  const name = (body?.name || 'DevezCode PC').slice(0, 80);
  if (!userCode) return Response.json({ error: 'userCode required' }, { status: 400 });

  const pair = await getPair(env, userCode);
  if (!pair || pair.status !== 'pending') return Response.json({ error: 'invalid or expired code' }, { status: 400 });

  const deviceId = crypto.randomUUID();
  const deviceSecret = randomToken(32);
  const secretHash = await sha256Hex(deviceSecret);

  await putDevice(env, deviceId, {
    ownerSub: session.ownerSub,
    email: session.email,
    secretHash,
    name,
    createdAt: Date.now(),
  });
  await addOwnerDevice(env, session.ownerSub, deviceId);
  await putPair(env, userCode, { status: 'approved', deviceId, deviceSecret }, SECRET_HANDOFF_TTL_SECONDS);

  return Response.json({ ok: true, deviceId });
}

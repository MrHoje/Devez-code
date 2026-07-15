// KV 고정 윈도 카운터 기반 단순 rate limiter. 로그인/페어링 시도 남용 방지용(스펙 7절).

export async function checkRateLimit(env, key, bucket, limit, windowSeconds) {
  const kvKey = `ratelimit:${bucket}:${key}`;
  const raw = await env.DEVICES.get(kvKey);
  const count = raw ? parseInt(raw, 10) : 0;
  if (count >= limit) return true;
  await env.DEVICES.put(kvKey, String(count + 1), { expirationTtl: windowSeconds });
  return false;
}

export function clientIp(request) {
  return request.headers.get('CF-Connecting-IP') || 'unknown';
}

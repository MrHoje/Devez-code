// Worker 라우터 진입점. 인증/API/WS/정적 자산으로 분기한다.

import { DeviceHub } from './device-hub.js';
import { handleAuthLogin, handleAuthCallback } from './auth/oauth.js';
import { buildLogoutCookie, requireSession } from './auth/session.js';
import { getDevice } from './kv.js';
import { handleSessionApi, handleDevicesApi, handleDeleteDevice } from './routes/devices.js';
import { handlePairStart, handlePairPoll, handlePairApprove } from './routes/pairing.js';
import { routeClientWs, routeDeviceWs } from './routes/ws.js';
import { checkRateLimit, clientIp } from './rateLimit.js';

export { DeviceHub };

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    try {
      if (url.pathname === '/auth/login') {
        if (await checkRateLimit(env, clientIp(request), 'auth-login', 20, 60)) return tooManyRequests();
        return handleAuthLogin(request, env);
      }
      if (url.pathname === '/auth/callback') return handleAuthCallback(request, env);
      if (url.pathname === '/auth/logout') return handleLogout();

      if (url.pathname === '/api/session') return handleSessionApi(request, env);
      if (url.pathname === '/api/devices') return handleDevicesApi(request, env);
      if (url.pathname.startsWith('/api/devices/') && request.method === 'DELETE')
        return handleDeleteDevice(request, env, decodeURIComponent(url.pathname.slice('/api/devices/'.length)));

      if (url.pathname === '/api/pair/start') {
        if (await checkRateLimit(env, clientIp(request), 'pair-start', 10, 60)) return tooManyRequests();
        return handlePairStart(request, env);
      }
      if (url.pathname === '/api/pair/poll') return handlePairPoll(request, env);
      if (url.pathname === '/api/pair/approve') {
        if (await checkRateLimit(env, clientIp(request), 'pair-approve', 10, 60)) return tooManyRequests();
        return handlePairApprove(request, env);
      }

      if (url.pathname.startsWith('/client/')) return routeClientWs(request, env, url.pathname.slice('/client/'.length));
      if (url.pathname.startsWith('/device/')) return routeDeviceWs(request, env, url.pathname.slice('/device/'.length));

      return serveStatic(request, env, url);
    } catch (err) {
      console.error('relay error', err);
      return new Response('internal error', { status: 500 });
    }
  },
};

function handleLogout() {
  const headers = new Headers({ Location: '/', 'Set-Cookie': buildLogoutCookie() });
  return new Response(null, { status: 302, headers });
}

function tooManyRequests() {
  return new Response('too many requests', { status: 429 });
}

async function serveStatic(request, env, url) {
  // /app/{deviceId} 는 정적 파일이 아니라 대시보드 SPA 셸을 반환한다.
  // Assets가 index.html을 /app/으로 정규화해도 대상 PC를 잃지 않도록 쿼리로 전달한다.
  if (url.pathname.startsWith('/app/') && !url.pathname.endsWith('.js') && !url.pathname.endsWith('.css')) {
    const shellUrl = new URL('/app/index.html', url);
    shellUrl.search = url.search;
    const deviceId = url.pathname.slice('/app/'.length) || url.searchParams.get('deviceId') || '';
    const session = await requireSession(request, env);
    if (!session) {
      const next = url.pathname + url.search;
      return new Response(null, { status: 302, headers: { Location: `/auth/login?next=${encodeURIComponent(next)}` } });
    }
    const device = deviceId ? await getDevice(env, deviceId) : null;
    if (!device || device.ownerSub !== session.ownerSub)
      return new Response('forbidden', { status: 403 });
    if (deviceId) shellUrl.searchParams.set('deviceId', deviceId);
    return env.ASSETS.fetch(new Request(shellUrl, request));
  }
  return env.ASSETS.fetch(request);
}

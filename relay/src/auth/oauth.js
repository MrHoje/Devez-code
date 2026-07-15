// Google OAuth 로그인/콜백. Authorization Code + PKCE(S256) + state(CSRF 방어).
// id_token 서명 검증 대신, 코드 교환으로 얻은 access_token 으로 Google userinfo 엔드포인트를
// 직접(TLS) 호출해 프로필을 얻는다 — 별도 JWKS 검증 없이도 신뢰 가능한 경로.

import { pkcePair, randomToken } from '../util.js';
import { signSession, buildSessionCookie } from './session.js';
import { isAllowedEmail } from '../kv.js';

const GOOGLE_AUTH_URL = 'https://accounts.google.com/o/oauth2/v2/auth';
const GOOGLE_TOKEN_URL = 'https://oauth2.googleapis.com/token';
const GOOGLE_USERINFO_URL = 'https://www.googleapis.com/oauth2/v3/userinfo';
const STATE_COOKIE = 'oauth_state';
const STATE_TTL_SECONDS = 600; // 10분

const redirectUri = url => `${url.protocol}//${url.host}/auth/callback`;

function readCookie(request, name) {
  const header = request.headers.get('Cookie') || '';
  for (const part of header.split(';')) {
    const idx = part.indexOf('=');
    if (idx < 0) continue;
    if (part.slice(0, idx).trim() === name) return part.slice(idx + 1).trim();
  }
  return null;
}

export async function handleAuthLogin(request, env) {
  const url = new URL(request.url);
  const next = url.searchParams.get('next') || '/';
  const state = randomToken(16);
  const { verifier, challenge } = await pkcePair();
  await env.DEVICES.put(`oauthState:${state}`, JSON.stringify({ verifier, next }), { expirationTtl: STATE_TTL_SECONDS });

  const authorize = new URL(GOOGLE_AUTH_URL);
  authorize.searchParams.set('client_id', env.GOOGLE_CLIENT_ID);
  authorize.searchParams.set('redirect_uri', redirectUri(url));
  authorize.searchParams.set('response_type', 'code');
  authorize.searchParams.set('scope', 'openid email profile');
  authorize.searchParams.set('state', state);
  authorize.searchParams.set('code_challenge', challenge);
  authorize.searchParams.set('code_challenge_method', 'S256');
  authorize.searchParams.set('access_type', 'online');
  authorize.searchParams.set('prompt', 'select_account');

  const headers = new Headers({ Location: authorize.toString() });
  headers.append('Set-Cookie', `${STATE_COOKIE}=${state}; HttpOnly; Secure; SameSite=Lax; Path=/auth; Max-Age=${STATE_TTL_SECONDS}`);
  return new Response(null, { status: 302, headers });
}

export async function handleAuthCallback(request, env) {
  const url = new URL(request.url);
  const code = url.searchParams.get('code');
  const state = url.searchParams.get('state');
  const cookieState = readCookie(request, STATE_COOKIE);
  if (!code || !state || !cookieState || state !== cookieState) {
    return new Response('로그인 요청이 유효하지 않습니다(state 불일치). 다시 로그인해 주세요.', { status: 400 });
  }

  const storedRaw = await env.DEVICES.get(`oauthState:${state}`);
  if (!storedRaw) return new Response('로그인 요청이 만료되었습니다. 다시 로그인해 주세요.', { status: 400 });
  const { verifier, next } = JSON.parse(storedRaw);
  await env.DEVICES.delete(`oauthState:${state}`);

  const tokenRes = await fetch(GOOGLE_TOKEN_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      code,
      client_id: env.GOOGLE_CLIENT_ID,
      client_secret: env.GOOGLE_CLIENT_SECRET,
      redirect_uri: redirectUri(url),
      grant_type: 'authorization_code',
      code_verifier: verifier,
    }),
  });
  if (!tokenRes.ok) return new Response('Google 토큰 교환에 실패했습니다.', { status: 502 });
  const tokenJson = await tokenRes.json();

  const userRes = await fetch(GOOGLE_USERINFO_URL, {
    headers: { Authorization: `Bearer ${tokenJson.access_token}` },
  });
  if (!userRes.ok) return new Response('Google 사용자 정보 조회에 실패했습니다.', { status: 502 });
  const profile = await userRes.json();

  if (!profile.email_verified || !(await isAllowedEmail(env, profile.email))) {
    // 이메일 allowlist 이중 차단(스펙 4.3).
    return new Response('허용되지 않은 계정입니다. 관리자에게 문의하세요.', { status: 403 });
  }

  const token = await signSession(env, { ownerSub: profile.sub, email: profile.email });
  const headers = new Headers({ Location: next || '/' });
  headers.append('Set-Cookie', buildSessionCookie(token));
  headers.append('Set-Cookie', `${STATE_COOKIE}=; HttpOnly; Secure; SameSite=Lax; Path=/auth; Max-Age=0`);
  return new Response(null, { status: 302, headers });
}

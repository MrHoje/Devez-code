// 세션 JWT(HS256, 자체 서명) 발급/검증 + 쿠키 처리. 클레임: {ownerSub, email, iat, exp}.

import { base64UrlFromBytes, base64UrlToBytes } from '../util.js';

export const SESSION_COOKIE = 'devez_session';
const SESSION_TTL_SECONDS = 12 * 60 * 60; // 12시간(스펙 4.3)

const textToBytes = text => new TextEncoder().encode(text);
const bytesToText = bytes => new TextDecoder().decode(bytes);

async function hmacKey(env) {
  return crypto.subtle.importKey('raw', textToBytes(env.SESSION_SIGNING_KEY), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign', 'verify']);
}

export async function signSession(env, claims) {
  const now = Math.floor(Date.now() / 1000);
  const header = { alg: 'HS256', typ: 'JWT' };
  const payload = { ...claims, iat: now, exp: now + SESSION_TTL_SECONDS };
  const encHeader = base64UrlFromBytes(textToBytes(JSON.stringify(header)));
  const encPayload = base64UrlFromBytes(textToBytes(JSON.stringify(payload)));
  const signingInput = `${encHeader}.${encPayload}`;
  const sig = await crypto.subtle.sign('HMAC', await hmacKey(env), textToBytes(signingInput));
  return `${signingInput}.${base64UrlFromBytes(new Uint8Array(sig))}`;
}

export async function verifySession(env, token) {
  if (!token) return null;
  const parts = token.split('.');
  if (parts.length !== 3) return null;
  const [encHeader, encPayload, encSig] = parts;
  const valid = await crypto.subtle.verify('HMAC', await hmacKey(env), base64UrlToBytes(encSig), textToBytes(`${encHeader}.${encPayload}`));
  if (!valid) return null;
  let payload;
  try {
    payload = JSON.parse(bytesToText(base64UrlToBytes(encPayload)));
  } catch {
    return null;
  }
  if (!payload.exp || payload.exp < Math.floor(Date.now() / 1000)) return null;
  return payload;
}

export function parseCookies(request) {
  const header = request.headers.get('Cookie') || '';
  const out = {};
  for (const part of header.split(';')) {
    const idx = part.indexOf('=');
    if (idx < 0) continue;
    out[part.slice(0, idx).trim()] = part.slice(idx + 1).trim();
  }
  return out;
}

export async function requireSession(request, env) {
  const cookies = parseCookies(request);
  return verifySession(env, cookies[SESSION_COOKIE]);
}

export function buildSessionCookie(token) {
  return `${SESSION_COOKIE}=${token}; HttpOnly; Secure; SameSite=Lax; Path=/; Max-Age=${SESSION_TTL_SECONDS}`;
}

export function buildLogoutCookie() {
  return `${SESSION_COOKIE}=; HttpOnly; Secure; SameSite=Lax; Path=/; Max-Age=0`;
}

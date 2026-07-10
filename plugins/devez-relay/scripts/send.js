#!/usr/bin/env node
/*
 * devez-relay: /devez-send — 기존 세션에 지시를 전달한다.
 *
 * 두 가지 호출 방식:
 *  (A) 맥락 정리 모드(권장, /devez-send 커맨드가 사용):
 *        node send.js --target "<세션이름>" --brief-file "<지시문파일>"
 *      A 세션의 Claude 가 대화 맥락을 정리한 자립적 지시문을 파일로 저장해 넘긴다.
 *  (B) verbatim 모드(폴백):
 *        node send.js <세션이름> <메시지…>
 *      친 문장을 그대로 전달. 이름은 인덱스와 공백 무시 매칭.
 *
 * 공통: 자기(A) roomId = 환경변수 DEVEZCODE_ROOM_ID. 대상은 sessions-index.json 에서
 * 이름으로 해석(같은 프로젝트 우선 → 전체). 결과는 commands\<uuid>.json 으로 드롭.
 */
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');

function fail(msg) { console.error(msg); process.exit(1); }

// ── 플래그 파싱 ────────────────────────────────────────────────
const argv = process.argv.slice(2);
let targetFlag = null, briefFile = null; const rest = [];
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--target') targetFlag = argv[++i];
  else if (argv[i] === '--brief-file') briefFile = argv[++i];
  else rest.push(argv[i]);
}

const base = path.join(
  process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'),
  'DevezCode');

const indexPath = path.join(base, 'sessions-index.json');
let index;
try {
  index = JSON.parse(fs.readFileSync(indexPath, 'utf8'));
  if (!Array.isArray(index)) throw new Error('bad index');
} catch {
  fail('❌ DevezCode 세션 인덱스를 읽을 수 없습니다. DevezCode 가 실행 중인지 확인하세요.');
}

const myRoom = process.env.DEVEZCODE_ROOM_ID || '';
const me = index.find(s => s.roomId === myRoom);
const myProject = me ? me.projectPath : null;

// 공백 무시 이름 매핑(“세션2” == “세션 2”).
const norm = s => String(s || '').replace(/\s+/g, '');
const nameByNorm = new Map();
for (const s of index) {
  const n = String(s.name || '');
  if (n && !nameByNorm.has(norm(n))) nameByNorm.set(norm(n), n);
}

let targetName = null;
let message = '';

if (briefFile !== null) {
  // (A) 맥락 정리 모드
  if (!targetFlag) fail('❌ --target "<세션이름>" 이 필요합니다.');
  try { message = fs.readFileSync(briefFile, 'utf8'); }
  catch { fail('❌ 지시문 파일을 읽을 수 없습니다: ' + briefFile); }
  message = message.trim();
  if (!message) fail('❌ 지시문이 비어 있습니다.');
  targetName = nameByNorm.get(norm(targetFlag)) || null;
  if (targetName === null) fail(`❌ '${targetFlag}' 세션을 찾을 수 없습니다.`);
} else {
  // (B) verbatim 모드 — 앞쪽 토큰을 늘려가며 공백 무시로 실제 이름 매칭(가장 긴 매칭).
  const raw = rest.join(' ').trim();
  if (!raw) fail('사용법: /devez-send <세션이름> <메시지>');
  const tokens = raw.split(/\s+/).filter(Boolean);
  let consumed = 0;
  for (let k = 1; k <= tokens.length; k++) {
    const cand = norm(tokens.slice(0, k).join(''));
    if (nameByNorm.has(cand)) { targetName = nameByNorm.get(cand); consumed = k; }
  }
  if (targetName !== null) {
    message = tokens.slice(consumed).join(' ').trim();
  } else {
    targetName = tokens[0] || '';
    message = tokens.slice(1).join(' ').trim();
  }
  if (!targetName) fail('사용법: /devez-send <세션이름> <메시지>');
  if (!message) fail(`'${targetName}' 로 보낼 메시지가 비어 있습니다.`);
}

// ── 스코프 해석: 같은 프로젝트 우선 → 없으면 전체 ──────────────
let matches = [];
if (myProject) matches = index.filter(s => s.projectPath === myProject && s.name === targetName);
if (matches.length === 0) matches = index.filter(s => s.name === targetName);

if (matches.length === 0) fail(`❌ '${targetName}' 세션을 찾을 수 없습니다.`);
if (matches.length > 1) {
  const where = matches.map(s => s.projectName || s.projectPath || '?').join(', ');
  fail(`❌ '${targetName}' 세션이 여러 개입니다: ${where}. (같은 프로젝트 안에서 유일해야 합니다)`);
}

const target = matches[0];
if (target.roomId === myRoom) fail('❌ 자기 자신에게는 보낼 수 없습니다.');

// 명령 파일 드롭 — .tmp → .json rename(원자적).
const cmdDir = path.join(base, 'commands');
try { fs.mkdirSync(cmdDir, { recursive: true }); } catch {}
const id = crypto.randomUUID();
const payload = JSON.stringify({ targetRoomId: target.roomId, message, submit: true });
const tmp = path.join(cmdDir, id + '.tmp');
const fin = path.join(cmdDir, id + '.json');
try { fs.writeFileSync(tmp, payload, 'utf8'); fs.renameSync(tmp, fin); }
catch (e) { fail('❌ 명령 파일을 쓰지 못했습니다: ' + (e && e.message)); }

const preview = message.length > 60 ? message.slice(0, 60) + '…' : message;
console.log(`✅ '${targetName}' 세션으로 전달했습니다: ${preview}`);

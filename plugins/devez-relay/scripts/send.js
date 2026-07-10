#!/usr/bin/env node
/*
 * devez-relay: /send-to <세션이름> <메시지>
 *
 * DevezCode 세션 A(이 스크립트가 도는 곳)에서 다른 세션 B 로 지시를 전달한다.
 * 모델 개입 없이 슬래시 커맨드의 ! 라인에서 즉시 실행된다.
 *
 * 동작:
 *  1) 자기(A) roomId = 환경변수 DEVEZCODE_ROOM_ID (DevezCode 가 세션마다 주입)
 *  2) %AppData%\DevezCode\sessions-index.json 을 읽어 세션 목록 확보
 *  3) 대상 이름 해석: A 와 같은 프로젝트에서 먼저, 없으면 전체
 *  4) %AppData%\DevezCode\commands\<uuid>.json 에 { targetRoomId, message } 드롭
 *     (DevezCode 의 SessionCommandInboxService 가 감지해 B 터미널에 주입)
 *
 * 해석 실패(대상 없음/모호/자기자신)는 stderr 로 즉시 A 터미널에 표시하고 파일을 만들지 않는다.
 */
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');

function fail(msg) { console.error(msg); process.exit(1); }

// 커맨드가 "$ARGUMENTS"(한 인자) 또는 $ARGUMENTS(여러 인자) 어느 쪽으로 넘겨도
// slice(2).join(' ') 로 원문을 복원한다.
const raw = process.argv.slice(2).join(' ').trim();
if (!raw) fail('사용법: /send-to <세션이름> <메시지>');

const m = raw.match(/^(\S+)\s+([\s\S]+)$/);
if (!m) fail('메시지가 비어 있습니다. 사용법: /send-to <세션이름> <메시지>');
const targetName = m[1];
const message = m[2].trim();
if (!message) fail('메시지가 비어 있습니다. 사용법: /send-to <세션이름> <메시지>');

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

// 같은 프로젝트 우선 → 없으면 전체
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

// 명령 파일 드롭 — .tmp 로 쓴 뒤 .json 으로 rename(원자적 완성 → 감시자가 부분쓰기를 보지 않음)
const cmdDir = path.join(base, 'commands');
try { fs.mkdirSync(cmdDir, { recursive: true }); } catch {}
const id = crypto.randomUUID();
const payload = JSON.stringify({ targetRoomId: target.roomId, message, submit: true, from: myRoom });
const tmp = path.join(cmdDir, id + '.tmp');
const fin = path.join(cmdDir, id + '.json');
try {
  fs.writeFileSync(tmp, payload, 'utf8');
  fs.renameSync(tmp, fin);
} catch (e) {
  fail('❌ 명령 파일을 쓰지 못했습니다: ' + (e && e.message));
}

console.log(`✅ '${targetName}' 세션으로 전달했습니다: ${message}`);

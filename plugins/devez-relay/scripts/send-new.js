#!/usr/bin/env node
/*
 * devez-relay: /devez-send-new — 현재 세션(A)의 "자식 세션"을 만들어 작업을 위임한다.
 *
 * A 세션의 Claude 가 대화 맥락에서 자립적 브리핑을 작성해 파일로 저장한 뒤 이 스크립트를 호출한다:
 *   node send-new.js --brief-file <브리핑파일>
 *
 * 동작:
 *  1) 부모(A) roomId = 환경변수 DEVEZCODE_ROOM_ID (DevezCode 가 세션마다 주입)
 *  2) 브리핑 텍스트를 읽어(멀티라인 OK)
 *  3) %AppData%\DevezCode\commands\<uuid>.json 에 { action:"new-child", parentRoomId, message:브리핑 } 드롭
 *     → DevezCode(SessionCommandInboxService)가 A의 자식 세션을 만들어 부팅 후 브리핑을 주입하고,
 *       자식 완료 시 A로 완료 알림을 돌려준다.
 */
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');

function fail(m) { console.error(m); process.exit(1); }

// --brief-file <path> (권장) 또는 나머지 인자를 브리핑으로.
const argv = process.argv.slice(2);
let briefFile = null; let sessionName = ''; const rest = [];
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--brief-file') briefFile = argv[++i];
  else if (argv[i] === '--name') sessionName = argv[++i] || '';
  else rest.push(argv[i]);
}
sessionName = sessionName.trim().slice(0, 15);   // 세션명 15자 이내

let brief = '';
if (briefFile) {
  try { brief = fs.readFileSync(briefFile, 'utf8'); }
  catch { fail('❌ 브리핑 파일을 읽을 수 없습니다: ' + briefFile); }
} else {
  brief = rest.join(' ');
}
brief = (brief || '').trim();
if (!brief) fail('❌ 브리핑이 비어 있습니다. --brief-file <경로> 로 작업 지시서를 전달하세요.');

const parent = process.env.DEVEZCODE_ROOM_ID || '';
if (!parent) fail('❌ 부모 세션을 식별할 수 없습니다(DEVEZCODE_ROOM_ID 없음). DevezCode 세션 안에서 실행해야 합니다.');

const base = path.join(
  process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'),
  'DevezCode');
const cmdDir = path.join(base, 'commands');
try { fs.mkdirSync(cmdDir, { recursive: true }); } catch {}

const id = crypto.randomUUID();
const payload = JSON.stringify({
  action: 'new-child',
  parentRoomId: parent,
  sessionName: sessionName,
  message: brief,
  submit: true,
});
const tmp = path.join(cmdDir, id + '.tmp');
const fin = path.join(cmdDir, id + '.json');
try { fs.writeFileSync(tmp, payload, 'utf8'); fs.renameSync(tmp, fin); }
catch (e) { fail('❌ 명령 파일 쓰기 실패: ' + (e && e.message)); }

console.log('✅ 자식 세션 생성을 요청했습니다. DevezCode 가 새 세션을 만들어 브리핑을 지시하며, 완료되면 이 세션으로 알림이 돌아옵니다.');

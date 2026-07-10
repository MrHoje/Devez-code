#!/usr/bin/env node
/*
 * devez-relay: /devez-pick — 다른 세션의 "대화 내용"을 읽어 현재 세션이 숙지하게 한다.
 *
 *   node devez-pick.js --session "<세션이름>"  [--last <N>]
 *
 * 동작:
 *  1) 세션 이름 → roomId (sessions-index.json, 공백 무시 매칭, 같은 프로젝트 우선)
 *  2) roomId → 그 방의 현재 claude 세션ID (%AppData%\DevezCode\claude\sessions\<roomId>.txt, SessionStart 훅이 기록)
 *  3) 세션ID + 프로젝트경로 → transcript (~/.claude/projects/<slug>/<sessionId>.jsonl)
 *  4) transcript 를 사람이 읽을 요약(사용자/Claude 텍스트 + 도구사용 표시)으로 정리해 파일로 저장하고 경로를 출력
 *
 * (claude 세션 전용 — 다른 에이전트는 transcript 위치/형식이 달라 미지원. child 세션은 transcript 자체가 없음.)
 */
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');

function fail(m) { console.error(m); process.exit(1); }

// ── 인자 ──
const argv = process.argv.slice(2);
let sessionArg = null, lastN = 0;
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--session') sessionArg = argv[++i];
  else if (argv[i] === '--last') lastN = parseInt(argv[++i], 10) || 0;
  else if (!sessionArg) sessionArg = argv[i];
}
sessionArg = (sessionArg || '').trim();
if (!sessionArg) fail('사용법: node devez-pick.js --session "<세션이름>" [--last N]');

const base = path.join(process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'), 'DevezCode');

// ── 1) 이름 → roomId ──
let index;
try { index = JSON.parse(fs.readFileSync(path.join(base, 'sessions-index.json'), 'utf8')); if (!Array.isArray(index)) throw 0; }
catch { fail('❌ 세션 인덱스를 읽을 수 없습니다. DevezCode 가 실행 중인지 확인하세요.'); }

const norm = s => String(s || '').replace(/\s+/g, '');
const target = norm(sessionArg);
const myRoom = process.env.DEVEZCODE_ROOM_ID || '';
const me = index.find(s => s.roomId === myRoom);
const myProject = me ? me.projectPath : null;

let matches = index.filter(s => norm(s.name) === target);
if (myProject && matches.some(s => s.projectPath === myProject))
  matches = matches.filter(s => s.projectPath === myProject);   // 같은 프로젝트 우선
if (matches.length === 0) fail(`❌ '${sessionArg}' 세션을 찾을 수 없습니다.`);
if (matches.length > 1) fail(`❌ '${sessionArg}' 세션이 여러 개입니다: ${matches.map(s => s.projectName || s.projectPath).join(', ')}`);
const sess = matches[0];

// ── 2) roomId → claude 세션ID ──
const safeRoom = String(sess.roomId).replace(/[^\w\-]/g, '');
const sidFile = path.join(base, 'claude', 'sessions', safeRoom + '.txt');
let sessionId;
try { sessionId = fs.readFileSync(sidFile, 'utf8').trim(); }
catch { fail(`❌ '${sess.name}' 의 claude 세션 기록이 없습니다. (그 세션을 한 번 이상 열어 실행했는지, claude 에이전트인지 확인하세요)`); }
if (!sessionId) fail(`❌ '${sess.name}' 의 세션ID가 비어 있습니다.`);

// ── 3) transcript 경로 ──
const slug = String(sess.projectPath).replace(/[^a-zA-Z0-9]/g, '-');
const transcript = path.join(os.homedir(), '.claude', 'projects', slug, sessionId + '.jsonl');
if (!fs.existsSync(transcript))
  fail(`❌ transcript 파일이 없습니다: ${transcript}\n(그 세션이 child 세션이면 대화 로그가 저장되지 않습니다)`);

// ── 4) transcript → 읽기 좋은 요약 ──
function textFromContent(content) {
  const out = [];
  if (typeof content === 'string') { if (content.trim()) out.push(content.trim()); return out; }
  if (Array.isArray(content)) {
    for (const b of content) {
      if (!b || typeof b !== 'object') continue;
      if (b.type === 'text' && b.text && b.text.trim()) out.push(b.text.trim());
      else if (b.type === 'tool_use') out.push(`[도구 사용: ${b.name || '?'}]`);
      // tool_result 는 잡음이라 생략
    }
  }
  return out;
}

// 슬래시커맨드 입력 마커(<command-name>.../<command-args>...)를 짧게 정리. 확장 원문(isMeta)은 위에서 통째 제거됨.
function cleanCommandMarkers(text) {
  if (!/<command-name>/.test(text)) return text;
  const name = (text.match(/<command-name>([^<]*)<\/command-name>/) || [, ''])[1].trim();
  const args = (text.match(/<command-args>([\s\S]*?)<\/command-args>/) || [, ''])[1].trim();
  return `[명령: ${name}${args ? ' ' + args : ''}]`;
}

const lines = fs.readFileSync(transcript, 'utf8').split(/\r?\n/);
const turns = [];
for (const line of lines) {
  if (!line.trim()) continue;
  let obj; try { obj = JSON.parse(line); } catch { continue; }
  if (obj.isMeta === true) continue;              // 슬래시커맨드 확장 등 합성 메시지 = 노이즈 → 제거
  const t = obj.type;
  if (t !== 'user' && t !== 'assistant') continue;
  const msg = obj.message || {};
  const role = msg.role || t;
  const texts = textFromContent(msg.content).map(cleanCommandMarkers);
  // 텍스트 없음(사용자 tool_result 전용 턴)은 건너뜀
  if (texts.length === 0) continue;
  const onlyToolMarkers = texts.every(x => x.startsWith('[도구 사용:'));
  if (role === 'user' && onlyToolMarkers) continue;
  turns.push({ role, text: texts.join('\n') });
}

let picked = turns;
if (lastN > 0 && turns.length > lastN) picked = turns.slice(-lastN);

const label = r => (r === 'assistant' ? 'Claude' : '사용자');
const body = picked.map(t => `=== ${label(t.role)} ===\n${t.text}`).join('\n\n');
const header = `# 세션 '${sess.name}' 대화 내용\n`
  + `- 프로젝트: ${sess.projectName || sess.projectPath}\n`
  + `- sessionId: ${sessionId}\n`
  + `- 총 턴: ${turns.length}${lastN > 0 ? ` (최근 ${picked.length}개만 표시)` : ''}\n\n`;

const outDir = path.join(base, 'picks');
try { fs.mkdirSync(outDir, { recursive: true }); } catch {}
const outFile = path.join(outDir, crypto.randomUUID() + '.md');
fs.writeFileSync(outFile, header + body, 'utf8');

console.log(`✅ '${sess.name}' 세션 대화를 정리했습니다 (턴 ${turns.length}개, ${Math.round((header.length + body.length) / 1024)}KB).`);
console.log(`읽을 파일: ${outFile}`);

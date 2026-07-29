# claude 세션은 터미널 내 검색(Ctrl+Shift+F)이 원리적으로 불가 — 대체화면 버퍼

## 결론

**claude 세션은 대체화면 버퍼(alternate screen, `DECSET ?1049h`)에서 돌아간다.
그래서 xterm 버퍼에는 "지금 보이는 행"만 존재하고 스크롤백이 없다.**
xterm `SearchAddon` 은 `term.buffer.active` 밖을 볼 방법이 없으므로,
claude 대화 히스토리를 터미널 검색으로 찾는 것은 **구현 난이도 문제가 아니라 불가능**이다.

→ 2026-07-29, 터미널 내 검색 기능(검색바 + `Ctrl+Shift+F` + `SearchAddon`)을 통째로 제거했다.

## 왜 헷갈리는가 (오진 주의)

검색 코드 자체는 **버퍼 전체**를 대상으로 한다. 그래서 "뷰포트만 검색한다"는 코드는 어디에도 없다.
`addon-search` 의 `findNextWithSelection` 은 선택이 없으면 `startRow = 0` 으로 시작해
아래로 훑고 다시 위로 랩어라운드한다. `_selectResult` 는 매치가 화면 밖이면 `scrollLines` 로 스크롤한다.
즉 **코드를 읽는 것만으로는 절대 원인을 못 찾는다.** 버퍼에 데이터가 없는 게 원인이다.

증상: "위로 스크롤한 뒤 검색하면 그 위쪽 것들이 조회된다."
이건 두 가지로 설명되어 구별이 안 된다 —
* (A) 대체화면. 휠이 claude 로 전달돼 **claude 가 자기 히스토리를 현재 프레임에 다시 그린다.**
  그러면 그 내용이 "지금 화면"이 되니 검색된다. xterm 버퍼는 여전히 보이는 행만.
* (B) 일반 버퍼. 스크롤백에 다 있는데 검색 도달 범위만 막혀 있다.

## A/B 판별법 (빌드 불필요, 30초)

앱을 끄거나 진단 코드를 넣지 않고 구별할 수 있다.

1. **Ctrl+Shift+A → Ctrl+Shift+C → 메모장 붙여넣기** (가장 확실)
   xterm 버퍼 전체를 그대로 뽑는다. **딱 한 화면(수십 줄)만 나오면 A.** 수천 줄이면 B.
2. **Ctrl+Shift+Home** (`term.scrollToTop()` — 마우스 모드를 안 거치는 xterm 직접 스크롤)
   아무 변화 없으면 A. 세션 맨 처음까지 올라가면 B.

claude 로 실측 결과 = **A**.

## 에이전트별로 갈리는 이유

`terminal.html` 은 `gajae` 방에 한해 파서 단계에서 **마우스 모드 DECSET 과 `CSI 3J`(스크롤백 클리어)를 삼킨다**
(`swallowMouse` / `swallowScrollbackClear` 로 grep). `codex` 도 인라인 TUI 로 취급해 `follow` 추종 로직을 태운다.
그 결과 codex/gjc 는 **실제 xterm 스크롤백**을 쓴다 → 거기선 터미널 검색이 전체 대상으로 동작했다.
claude 는 둘 다 아니고 `?1049` 도 통과시키므로(마우스 모드 주석 참고) 스크롤 주체가 claude 본인이다.

> 기능 제거는 claude 기준 판단이다. codex/gjc 에서는 동작하던 기능을 함께 걷어낸 것 — 되살릴 땐 이 차이를 기억할 것.

## 버린 대안 (다시 검토하지 말 것)

* **`?1049` 삼키기** → claude TUI 는 화면 좌표 기준으로 렌더한다. 일반 버퍼로 흘리면 매 프레임 중복 잔재가
  스크롤백에 쌓여 검색 결과가 같은 텍스트 수십 벌로 오염되고, 렌더 자체가 깨질 위험이 크다.
* **PTY 원시 출력을 따로 tee 해서 검색** → 같은 이유로 중복 프레임 덩어리. ANSI 를 스트립해도 잡음뿐.

## 다시 만들려면 — transcript 검색 (유일한 길)

터미널 버퍼가 아니라 **에이전트가 남기는 대화 로그**를 검색해야 한다. `SessionExporter` 가 이미 전 에이전트
포맷을 파싱하므로 그걸 재사용한다 (`Services/SessionExporter.cs`, `FromClaude` / `FindClaudeTranscriptPath`).

정확도와 제약 (실측 포함):
* **대화 본문(내 프롬프트 + 응답 텍스트)** 은 거의 100%. 5000줄 스크롤백 제약도, 화면 제약도 없고
  화면에서 `… +12 lines` 로 접혔던 원문까지 잡힌다 — 터미널 검색보다 넓다.
* **툴 결과는 안 잡힌다.** `ExtractContentText` 가 `type=="text"` 블록과 문자열 content 만 모은다.
  실측(claude jsonl 1개, 366줄/12.1MB): `text 38 / string 17` 은 잡히고
  `thinking 47 / tool_use 49 / tool_result 49 / document 2 / image 1` 은 전부 버려진다.
  → 파일 diff, bash 출력, grep 결과, Read 내용을 못 찾는다. **이 확장 없이 만들면
  "화면에서 본 걸 못 찾는다"는 같은 불만이 재발한다.**
* **레코드 타입**도 `user`/`assistant` 만 쓴다. `system` / `attachment` / `last-prompt` 등은 버려진다.
* **파일이 여러 개로 갈린다.** fork 는 새 GUID jsonl 을 만들고 `/clear` 도 파일을 바꾼다.
  단일 sid 파일만 뒤지면 그 이전 히스토리가 유실되고, ID 가 어긋나면 **조용히 0건**이 된다.
  cwd 인코딩 폴더 전체를 훑고 세션별로 그룹핑해야 한다.
* **성능**: 한 줄이 수십 KB(12MB / 366줄). 키스트로크마다 full `JsonDocument.Parse` 는 UI 를 멈춘다.
  백그라운드 + 디바운스 + 파일 길이 워터마크 증분 캐시 + `CancellationToken` 필수.
* **지연 flush**: 방금 오간 마지막 1~2 턴은 아직 파일에 없다.
* **에이전트 범위**: 파일 직독인 claude/codex/gajae/kimi 만. `opencode` 는 `opencode export`,
  `grok` 은 SQLite export 로 **프로세스를 스폰**하므로 검색마다 띄우면 느리고 불안정하다.
* **잠긴 파일**은 이미 `FileShare.ReadWrite` 로 해결되어 있다.

UI 제약 (중요): transcript 오프셋 → xterm 버퍼 행 **매핑이 불가능**하다. claude 가 화면에 무엇을 어떻게
그렸는지 알 수 없다. 따라서 **히트를 터미널에서 하이라이트하거나 그 위치로 점프할 수 없다.**
별도 결과 패널(역할·시각·스니펫 리스트)이어야 하고, 옛 `Ctrl+Shift+F` 의 "터미널 find" 의미와는 다른 물건이다.
같은 단축키에 두 의미를 얹지 말 것.

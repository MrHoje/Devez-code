# Devez CLI

Claude Code의 차분한 터미널 흐름을 참고해 새로 만든 Codex `app-server` 클라이언트입니다.
Codex의 인증, 하네스 프롬프트, 도구, 스킬, `AGENTS.md`, 샌드박스는 공식
`codex app-server`가 그대로 담당합니다. 이 프로젝트는 화면과 입력 계층만 소유합니다.

## 현재 범위

- 새 스레드 시작 및 `--resume <THREAD_ID>`
- 모델 카탈로그 기반 `/model` 선택
- 서버가 지원하는 reasoning effort만 노출 (`max` 포함)
- 응답, reasoning summary, 명령, 파일 변경, MCP 호출 스트리밍
- 명령/파일 변경 승인
- 실행 중 입력 steer 및 `Esc`/`Ctrl+C` 중단
- 일반 터미널 스크롤백을 보존하는 증분 렌더링
- 시작 카드와 항상 보이는 composer/status bar
- `/` 명령 자동완성 및 키보드 모델 선택기
- Markdown 제목·목록·인용·코드 블록 표현
- 실행 시간, 파일 diff 통계, 진행 상태 표시
- 활성 영역 전체 삭제 없이 변경된 터미널 행만 갱신

## 실행

Codex CLI가 설치되고 로그인된 환경에서:

```powershell
cd cli
cargo run --release
```

주요 옵션:

```text
devez [--resume THREAD_ID] [--model MODEL] [--effort EFFORT]
      [--cwd PATH] [--codex PATH]
```

입력창 명령은 `/help`에서 확인할 수 있습니다.

## 경계

`app-server` 프로토콜은 Codex 버전에 따라 변할 수 있습니다. 렌더러 변경은 독립적으로
관리하고, 업스트림에서는 app-server 메서드/스키마/인증/모델 카탈로그 변경만 호환성
대상으로 봅니다.

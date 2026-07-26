# Devez CLI 표시 순서 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 모든 에이전트 목록에서 Devez CLI를 마지막으로 표시한다.

**Architecture:** 공통 목록의 순서 원천인 `AgentRegistry.All`에서 `devezcli` 정의를 배열의 마지막으로 이동한다. 설정과 세션 생성 피커는 이미 이 배열을 소비하므로 별도 정렬 로직은 만들지 않는다.

**Tech Stack:** C#, .NET/WPF

## Global Constraints

- `Devez CLI`의 ID, 미리보기 잠금, 설치 감지, 실행과 세션 복원 동작을 변경하지 않는다.
- 다른 에이전트의 상대 순서는 유지한다.
- 빌드 전 실행 중인 DevezCode 프로세스는 강제 종료하지 않는다.

---

### Task 1: 공통 에이전트 순서 변경

**Files:**
- Modify: `Services/AgentRegistry.cs`
- Test: 코드 검토 및 Release 빌드

**Interfaces:**
- Consumes: `AgentRegistry.All`의 선언 순서
- Produces: `devezcli`가 마지막인 공통 에이전트 목록

- [x] **Step 1: 변경 전 순서 확인**

Run: `rg -n 'Id = "devezcli"|Id = "grok"|Id = "antigravity"|Id = "kimi"' Services/AgentRegistry.cs`

Expected: `devezcli`가 다른 일반 에이전트 정의보다 앞에 있다.

- [x] **Step 2: 최소 구현 작성**

`AgentRegistry.All`에서 `devezcli` 객체 전체를 마지막 일반 에이전트 객체 뒤로 이동한다. 객체의 속성 값은 변경하지 않는다.

- [x] **Step 3: 결과 순서 확인**

Run: `rg -n 'Id = "devezcli"|Id = "grok"|Id = "antigravity"|Id = "kimi"' Services/AgentRegistry.cs`

Expected: `devezcli`의 행 번호가 모든 일반 에이전트 정의보다 크다.

- [ ] **Step 4: Release 빌드 확인**

Run: `dotnet build -c Release --nologo -v quiet`

Expected: exit code 0.

- [ ] **Step 5: 커밋과 푸시**

Run: `git add Services/AgentRegistry.cs docs/superpowers/plans/2026-07-26-devez-cli-order.md && git commit -m "fix(agent): place Devez CLI last" && git push`

Expected: 커밋과 원격 푸시가 성공한다.

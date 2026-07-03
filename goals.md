# Goals

공유 컨텍스트:
- 작업 경로: D:\hojeSource\Devez-code
- 빌드: `dotnet build -c Release --nologo -v quiet` (성공 시에만 진행)
- 완료 시 커밋+푸시 (Co-Authored-By 트레일러, 메시지는 파일로 작성해 `git commit -F`)
- 분할(split) = PaneA(좌)/PaneB(우) 2패널. 파일 에디터/터미널은 WebView2(airspace).

## G001: md 파일 탭 왼쪽 패널 포커스
**상태:** ✅ complete (구현+빌드검증, UI 동작은 사용자 확인 예정)
**완료 증거:** Interacted 구독을 CreateFileTab(생성 패널)→ActivateFileTab(표시 패널)로 이동,
`_activeTab==tab` 가드 + 패널별 1회 구독 dedup + RemoveFileTab 정리. Release 빌드 0 에러.
**목표:** md/파일 탭이 어느 패널에 있든 클릭하면 그 패널이 포커스(테두리)를 가져가게 한다.
**원인 가설:** `FileTabItem.Editor.Interacted` 핸들러가 탭 생성 패널(`this`)을 캡처.
에디터는 공유 단일 UserControl이라 다른 패널에서 표시·클릭 시 생성 패널의 FocusRequested가
발화 → 표시 중인 패널이 아닌 엉뚱한 패널이 포커스됨(우측 생성 시 좌측 클릭이 안 먹음).
**수용 기준:**
- [ ] 파일 탭이 좌측 패널에 있을 때 에디터 클릭 → 좌측 패널 포커스 테두리.
- [ ] 우측 패널에 있을 때도 동일 동작(회귀 없음).
- [ ] 세션 이동으로 파일이 다른 패널로 옮겨가도 표시 중인 패널이 포커스.
- [ ] 빌드 0 에러.

## G002: 분할 프로젝트 재진입 시 우측 터미널 리플로우 깨짐
**상태:** ✅ complete (구현+빌드검증, UI 동작은 사용자 확인 예정)
**완료 증거:** terminal.html show() 에 지연 재-fit(refitSoon: rAF²+60ms+180ms) 추가 →
재진입 시 프리매처 fit 로 좁게 굳던 cols 를 최종 폭으로 교정, onResize 가 ConPTY 로 정확한
크기 전파해 alt-screen TUI 풀폭 재렌더. Release 빌드 0 에러.
**참고:** "스크롤 위로 안 됨"은 claude alt-screen(스크롤백 없음) 특성 — 버그 아님.
세션이 패널 간 이동 시 xterm 인스턴스가 새로 생겨 로딩 스피너 잠깐/재렌더는 ConPTY 이동 한계.
**목표:** 분할 프로젝트 → 비분할 프로젝트 → 다시 분할 프로젝트로 돌아올 때 우측 패널 세션
터미널이 (a) 로딩 스피너 재발/재로그 없이 (b) 내용이 폭에 맞게(잘림 없이) (c) 스크롤 정상으로 복원.
**원인 가설:** 패널 재진입 시 `TerminalHostView.WireSession` reattach 경로의 resize 킥/리플로우가
xterm 그리드를 잘못된 폭으로 굳힘. reattach 판정·resize 타이밍·fit 재적용 점검.
**수용 기준:**
- [ ] 재진입 후 우측 터미널 내용이 우측 폭에 맞게 표시(오른쪽 짤림 없음).
- [ ] 스크롤백 위로 스크롤 가능.
- [ ] 이미 준비된 세션이면 로딩 스피너가 다시 돌지 않음(가능한 범위).
- [ ] 빌드 0 에러.

## G003: 빌드 + 커밋 + 푸시
**상태:** ✅ complete
**완료 증거:** Release 빌드 0 에러, 커밋 d478ad0, main 푸시 완료.
**목표:** G001·G002 반영 후 Release 빌드 성공 → 커밋 → 푸시.
**수용 기준:**
- [ ] `dotnet build -c Release` 0 에러.
- [ ] 커밋(Co-Authored-By) + push 완료.

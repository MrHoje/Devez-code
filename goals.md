# Goals — Grok Build 사용량 표시

공유 컨텍스트:
- 작업 경로: D:\hojeSource\Devez-code
- 방식: `GET https://cli-chat-proxy.grok.com/v1/billing` (open-grok-build 동일)
- 인증: `~/.grok/auth.json` OIDC (실측: used=781 / limit=15000)
- 빌드 금지 (사용자 요청)

## G001: GrokCredentialStore + GrokUsageService
**상태:** ✅ complete
**완료 증거:** `Services/GrokCredentialStore.cs`, `Services/GrokUsageService.cs` 추가. CLI auth.json 파싱·refresh·billing 폴링.

## G002: MainWindow 푸터·사이드바 배선
**상태:** ✅ complete
**완료 증거:** `_grok` Start/Dispose, case `"grok"`, BuildUsageCards, GrokPanel 월간 막대, divider, RefreshGrokUsage, GrokIconUri.

## G003: Settings UI
**상태:** ✅ complete
**완료 증거:** ShowFooterGrok, 연결 배지, 푸터 토글 load/save/dirty/revert.

## G004: 브라우저 OAuth 로그인 창
**상태:** ✅ complete
**완료 증거:** `Views/GrokLoginWindow.cs` (Codex 패턴, PKCE + localhost redirect 가로채기).
설정 계정 연결에 "로그인 / 재연결" 버튼. 성공 시 `GrokCredentialStore.Save` + `RefreshGrokUsage`.
CLI `~/.grok/auth.json` 도 계속 인식.

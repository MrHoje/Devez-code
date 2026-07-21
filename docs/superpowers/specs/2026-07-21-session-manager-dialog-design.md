# 세션 관리자 팝업 설계

날짜: 2026-07-21

## 목표
프로젝트 카드 우클릭 메뉴에 "세션 관리자"를 추가하고, 그 팝업에서 프로젝트의 모든 세션을 한눈에 보며 여러 세션을 골라 **숨기기 / 닫기(추적 중단) / 삭제**를 일괄 수행한다.

## 진입점 — `Views/SidebarView.xaml`
- 프로젝트 카드 `ContextMenu`에서 마지막 `<Separator/>` + "프로젝트 제거" **바로 위**에 `MenuItem "세션 관리자"` 추가(아이콘 `IconLayers` 계열).
- Click 핸들러 `SessionManager_Click` → 신규 이벤트 `event Action<ProjectItem>? SessionManagerRequested` 발생(`ItemOf<ProjectItem>`로 대상 프로젝트 획득).

## 팝업 — `Views/SessionManagerDialog.xaml(.cs)` (신규)
`ConfirmDialog`과 동일한 borderless `Window` 패턴: Owner=MainWindow, `WindowCenter.CenterOverOwner`, 헤더 드래그(`DragMove`), Esc 닫기, `AppStyles.xaml` 전역 스타일 사용(인라인 남발 금지).

레이아웃:
- **헤더**: "세션 관리자 — {프로젝트명}" + 우상단 닫기(X).
- **상단 바**: `전체 선택` 체크박스(3-state: 전체/부분/없음) + "선택 N개" 카운트. 전체 선택은 **선택 가능한(=잠금 아님) 세션만** 대상.
- **목록**(`ItemsControl` + `ScrollViewer`): 각 행 =
  - 체크박스(`IsChecked` ↔ 행 VM `IsChecked`; `IsLocked`이면 `IsEnabled=false`)
  - 상태 표시: alive/dead 점 ↔ busy 회전 스피너(`IsBusy`), 세션 row 톤 재사용
  - 세션명(`Name`)
  - 배지: 숨김(`Hidden`)이면 "숨김" 표시, 잠금(`IsLocked`)이면 자물쇠 아이콘(`IconLock`)
- **하단 액션 바**: `숨기기` · `닫기` · `삭제`(danger). 선택 0개면 3버튼 비활성. 우측에 `닫기`(다이얼로그 종료) 버튼.

행 VM: 각 `SessionItem`을 감싸는 경량 wrapper(`SessionRow`)에 `IsChecked`(NotifyBase) + 원본 `SessionItem` 참조. 원본의 `Name/Hidden/IsBusy/IsAlive/IsLocked`에 바인딩.

## 일괄 처리 — 기존 로직 재사용
팝업은 선택 UI만 담당하고, 위험 동작은 **검증된 기존 메서드에 위임**한다. MainWindow가 `SessionManagerRequested` 구독:

```
Sidebar.SessionManagerRequested += p => {
    var dlg = new SessionManagerDialog(p,
        onHide:   HideSessionsFromSidebar,   // 숨기기
        onClose:  StopTrackingSessions,      // 닫기(추적 중단, 기록 보존)
        onDelete: DeleteSessions);           // 삭제(기록 포함 영구 삭제)
    dlg.Owner = this; dlg.ShowDialog();
};
```
이 세 메서드는 이미 자체 확인 다이얼로그, 하위 세션 최상위 이동, 잠금 검사(`EnsureSessionSubtreeUnlocked`), 기록 보존/삭제를 처리한다. 각 액션 실행 시 확인창이 한 번 더 뜬다(의도된 동작).

## 상태 동기화
- `ProjectItem.Sessions`가 `ObservableCollection`이라 삭제/닫기 후 목록이 자동 갱신. 팝업은 이 컬렉션(+`HiddenSessions`)을 합쳐 행 VM 리스트를 구성하되, 액션 후 컬렉션 변화가 반영되도록 액션 콜백 뒤 목록을 재빌드한다.
- 액션 실행 후 선택 초기화, 팝업은 열린 채 유지.

## 범위 제외 (YAGNI)
- 세션 잠금/해제, 이름 변경, 포크, 내보내기 등은 팝업에 넣지 않음(세션 우클릭 메뉴에 이미 존재).
- 잠금 세션은 **표시만** 하고 선택/일괄 대상에서 제외.

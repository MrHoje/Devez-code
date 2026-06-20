# DevezCode 작업 지침

## 빌드 및 재시작 프로세스

코드 변경 후 항상 다음 프로세스를 따르십시오:

```powershell
# 1. 실행 중인 앱 중지. 실행 중이 아니면 오류 무시.
taskkill /IM DevezCode.exe /F 2>$null

# 2. Release 빌드.
dotnet build -c Release --nologo -v quiet

# 3. 빌드가 성공한 경우에만 앱 재시작.
Start-Process "bin\Release\DevezCode.exe"
```

빌드가 실패하면 앱을 재시작하지 마십시오. 먼저 빌드 오류를 수정하십시오.
오류의 원인이 직접 수정하지 않은 파일이라면 병행 세션이 수정 중일 수 있으니 빌드를 멈추고 대기.

## 참고 대상 (devez)

- "devez를 참고해서"라고 하면 프로젝트 상위 폴더의 `devez`를 참고한다.
  `devez`가 없으면 `talkremind_wpf`를 참고한다.

## 참고

- 원격 접속(RDP/Chrome Remote Desktop)에서는 GPU 합성 화면이 전달되지 않아 창이 안 보일 수 있다.
  `App.OnStartup`에서 원격 세션을 감지해 `RenderMode.SoftwareOnly`를 강제하므로 새 창을 만들 때 이 처리를 빠뜨리지 말 것.
- 디자인(테마/색상/폰트/아이콘/팝업)은 `C:\source\devez`의 디자인 시스템을 따른다. 새 UI도 `AppStyles.xaml`의 전역 스타일을 사용하고 인라인 스타일을 남발하지 말 것.

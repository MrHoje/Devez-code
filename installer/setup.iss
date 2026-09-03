#define AppName     "DevezCode"
#define AppVersion  "1.26.15"
#define AppExeName  "DevezCode.exe"
#define AppPublisher "Devez"

[Setup]
AppId={{7C1E5B2A-3D4F-4A6B-9C8D-2E1F0A9B8C7D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\{#AppName}
LicenseFile=LICENSE.txt
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=DevezCode_Setup_{#AppVersion}
SetupIconFile=..\Resources\Logos\app.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 번들 추출 경로를 환경변수로 옮기므로 설치 후 환경 변경을 브로드캐스트한다.
ChangesEnvironment=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로가기 만들기"; GroupDescription: "추가 작업:"

[Files]
; single-file publish 결과물(약 15MB, framework-dependent — WebView2/xterm/zstd 내장) 하나만 설치한다.
Source: "..\bin\win-x64\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Resources\Licenses\VisualStudio2017ImageLibraryEULA.rtf"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\Resources\Licenses\VisualStudio2022ImageLibraryEULA.rtf"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "LICENSE.txt"; DestDir: "{app}\licenses"; DestName: "DevezCode-LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; single-file exe 는 실행할 때마다 번들 내용을 %TEMP%\.net\DevezCode\<랜덤> 으로 풀어 실행한다.
; Defender 의 머신러닝 휴리스틱은 임시 폴더에서 추출·실행되는 미서명 바이너리를 Trojan 으로
; 오탐하므로(Trojan:Win32/Bearfoos.A!ml), 추출 위치를 설치 폴더 아래 고정 경로로 옮긴다.
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "DOTNET_BUNDLE_EXTRACT_BASE_DIR"; ValueData: "{app}\bundle"; Flags: preservestringtype uninsdeletevalue

[UninstallDelete]
Type: filesandordirs; Name: "{app}\bundle"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{#AppName} 실행"; Flags: nowait postinstall skipifsilent

[Code]
// [Run] 로 띄우는 첫 실행은 setup 프로세스의 환경을 물려받아 방금 쓴 레지스트리 값을 보지
// 못한다. setup 자신의 환경에도 같은 값을 넣어야 첫 실행부터 새 추출 경로를 쓴다.
function SetEnvironmentVariable(lpName, lpValue: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', ExpandConstant('{app}\bundle'));
end;

function IsDotNet9Installed: Boolean;
var
  Path: String;
  Names: TArrayOfString;
  i: Integer;
  FindRec: TFindRec;
begin
  Result := False;

  // .NET 호스트는 64비트 OS에서도 레지스트리를 32비트 뷰(WOW6432Node)에 기록한다.
  // 64비트 설치 모드에서 HKLM은 64비트 뷰를 가리켜 항상 실패하므로 HKLM32 를 먼저 조회.
  Path := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  if not RegGetValueNames(HKLM32, Path, Names) then
    RegGetValueNames(HKLM64, Path, Names);
  for i := 0 to GetArrayLength(Names) - 1 do
    if Copy(Names[i], 1, 2) = '9.' then
    begin
      Result := True;
      Exit;
    end;

  // 폴백: 공유 런타임 폴더 직접 확인
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), FindRec) then
  begin
    FindClose(FindRec);
    Result := True;
  end;
end;

function InitializeSetup: Boolean;
var
  Dummy: Integer;
begin
  Result := True;
  if not IsDotNet9Installed then
  begin
    if MsgBox(
      '.NET 9 Desktop Runtime이 설치되어 있지 않습니다.' + #13#10#13#10 +
      'DevezCode를 실행하려면 .NET 9 Desktop Runtime이 필요합니다.' + #13#10 +
      '지금 다운로드 페이지를 열고 설치를 중단할까요?',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/ko-kr/download/dotnet/9.0', '', '', SW_SHOWNORMAL, ewNoWait, Dummy);
      Result := False;
    end;
  end;
end;

#define AppName     "DevezCode"
#define AppVersion  "1.24.4"
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

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{#AppName} 실행"; Flags: nowait postinstall skipifsilent

[Code]
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

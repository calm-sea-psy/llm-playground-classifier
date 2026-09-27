; 문서 전산화 설치 스크립트 (Inno Setup 6). installer\build.ps1 이 /DAppVersion · /DAppDir 를 넘겨 컴파일
; - 관리자 권한 없이 %LocalAppData%\Programs\DocumentDigitizer (설치 폴더는 영문: Paddle 이 한글 · 공백 경로에서 모델 파일을 못 여는 경우가 있음)
; - 파일을 놓은 뒤 설치 도우미(Ollama · 모델 · OCR · 설정 · 자동 시작)를 콘솔 창으로 실행 (건너뛸 수 없음, 실패 단계는 시작 메뉴에서 다시)
; - 제거: 프로그램 폴더(OCR 환경 · Python 포함)는 모두 지우고, 데이터(DB · 설정 · 로그)는 물어봄, 문서 폴더의 원본 · 엑셀은 지우지 않음

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppDir
  #define AppDir "out\app"
#endif

[Setup]
AppId={{6B7C2E1A-4F3D-4C8B-9A2E-D1F0C5B8A7E3}
AppName=문서 전산화
AppVersion={#AppVersion}
AppVerName=문서 전산화 {#AppVersion}
AppPublisher=llm-playground-classifier
AppPublisherURL=https://github.com/calm-sea-psy/llm-playground-classifier
DefaultDirName={localappdata}\Programs\DocumentDigitizer
DisableDirPage=yes
DefaultGroupName=문서 전산화
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; Inno Setup 6.5+ 의 RedirectionGuard(연결 · 심볼릭 링크 통과 막기)는 자식 프로세스에 이어져 설치 도우미의 nvidia-smi(GPU 를 못 찾아
; CPU 구성으로 잘못 설정) · uv 의 Python 연결 폴더(os error 448)가 막힘. 관리자 권한 없이 설치하므로 막을 권한 상승 경계가 없어 끔
RedirectionGuard=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=DocumentDigitizer-setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile=info-before.txt
UninstallDisplayName=문서 전산화
UninstallDisplayIcon={app}\Digitizer.exe
; 실행 중이면 물어보고 끈 뒤 진행 (앱이 고정 이름 뮤텍스를 만듦, SingleInstance.RunningMutex). AppMutex 는 막기만 해서 [Code] 로
CloseApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로가기 만들기"; Flags: unchecked

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\문서 전산화"; Filename: "{app}\Digitizer.exe"
Name: "{group}\문서 전산화 설치 도우미"; Filename: "{app}\setup\setup-helper.cmd"; Comment: "Ollama · 모델 · OCR 설치를 다시 확인하고 빠진 것을 설치"
Name: "{group}\문서 전산화 제거"; Filename: "{uninstallexe}"
Name: "{userdesktop}\문서 전산화"; Filename: "{app}\Digitizer.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\setup\setup-helper.ps1"" {code:HelperArgs}"; StatusMsg: "설치 도우미 실행 중 (Ollama · 모델 · OCR, 콘솔 창에서 진행 상황 확인)"; Flags: waituntilterminated
Filename: "{app}\Digitizer.exe"; Description: "문서 전산화 실행"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
; 설치 뒤에 생긴 것 (OCR 환경 · Python · uv · OCR 모델 · 캐시)까지 프로그램 폴더 전체
Type: filesandordirs; Name: "{app}"

[Code]
const
  RunningMutex = 'DocumentDigitizerRunning';

// 실행 중이면 끄기 (OCR 자식 프로세스는 작업 개체라 같이 끝남, 처리 중이던 문서는 다음 시작 때 다시 처리). 끄지 않겠다고 하면 False
function StopRunningApp(Silent: Boolean): Boolean;
var
  Code, I: Integer;
begin
  Result := True;
  if not CheckForMutexes(RunningMutex) then Exit;
  if not Silent then
    if MsgBox('문서 전산화가 실행 중입니다. 끄고 계속할까요?' + #13#10 + '처리 중이던 문서는 다음에 실행할 때 다시 처리합니다.', mbConfirmation, MB_YESNO) <> IDYES then
    begin
      Result := False;
      Exit;
    end;
  if not Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Digitizer.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('taskkill 실행 실패: ' + SysErrorMessage(Code))
  else
    Log('taskkill 종료 코드 ' + IntToStr(Code));
  for I := 1 to 50 do
  begin
    if not CheckForMutexes(RunningMutex) then Exit;
    Sleep(100);
  end;
  Result := not CheckForMutexes(RunningMutex);
end;

function InitializeSetup(): Boolean;
begin
  Result := StopRunningApp(WizardSilent);
end;

function InitializeUninstall(): Boolean;
begin
  Result := StopRunningApp(UninstallSilent);
end;

// 조용한 설치(/SILENT · /VERYSILENT)는 묻지 않고 진행, 보통 설치는 끝나면 결과를 읽을 수 있게 엔터를 기다림
function HelperArgs(Param: String): String;
begin
  if WizardSilent then Result := '-Yes' else Result := '-Pause';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DocumentDigitizer');
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    DataDir := ExpandConstant('{localappdata}\Digitizer');
    if DirExists(DataDir) and (MsgBox('프로그램 데이터도 지울까요?' + #13#10 + #13#10 +
      '처리 기록 · 검수 결과(DB) · 설정 · 로그: ' + DataDir + #13#10 + #13#10 +
      '다시 설치해서 이어 쓰려면 [아니요] 를 누르세요. 문서 폴더(문서\문서 전산화)의 원본 · 엑셀 파일은 어느 쪽이든 지우지 않습니다.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(DataDir, True, True, True);
  end;
end;

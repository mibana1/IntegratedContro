; Build with scripts/build-installer.ps1. Payload contains redistributable files only.
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef PackageVersion
  #error PackageVersion is required
#endif
#ifndef PackageName
  #error PackageName is required
#endif
#ifndef InstallerAppId
  #define InstallerAppId "IntegratedContro.Desktop"
#endif

[Setup]
AppId={#InstallerAppId}
AppName=IntegratedContro
AppVersion={#PackageVersion}
AppVerName=IntegratedContro {#PackageVersion}
VersionInfoVersion={#PackageVersion}
DefaultDirName={localappdata}\Programs\IntegratedContro
DefaultGroupName=IntegratedContro
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UninstallDisplayIcon={app}\App\IntegratedContro.App.exe
OutputBaseFilename={#PackageName}
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes
InfoBeforeFile={#PayloadDir}\INSTALL.txt

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로가기 만들기"; GroupDescription: "바로가기:"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\IntegratedContro"; Filename: "{app}\App\IntegratedContro.App.exe"; WorkingDir: "{app}\App"
Name: "{group}\설치 및 서버 설정 안내"; Filename: "{app}\INSTALL.txt"
Name: "{autodesktop}\IntegratedContro"; Filename: "{app}\App\IntegratedContro.App.exe"; WorkingDir: "{app}\App"; Tasks: desktopicon

[Run]
Filename: "{app}\App\IntegratedContro.App.exe"; Description: "IntegratedContro 실행"; WorkingDir: "{app}\App"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  ServerMode: TInputOptionWizardPage;
  DataPage: TInputDirWizardPage;
  MediaPage: TInputFileWizardPage;
  ApiPage: TInputQueryWizardPage;

function UnderPath(Path, Parent: String): Boolean;
begin
  Path := Lowercase(RemoveBackslashUnlessRoot(ExpandFileName(Path)));
  Parent := Lowercase(RemoveBackslashUnlessRoot(ExpandFileName(Parent)));
  Result := (Path = Parent) or (Pos(AddBackslash(Parent), Path) = 1);
end;

function RunningInstallation: String;
var
  Services, Processes, Process: Variant;
  I: Integer;
  Path: String;
begin
  Result := '';
  try
    Services := GetActiveOleObject('WbemScripting.SWbemLocator');
  except
    Services := CreateOleObject('WbemScripting.SWbemLocator');
  end;
  try
    Services := Services.ConnectServer('.', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE ExecutablePath IS NOT NULL');
    for I := 0 to Processes.Count - 1 do begin
      Process := Processes.ItemIndex(I);
      Path := Process.ExecutablePath;
      if UnderPath(Path, ExpandConstant('{app}')) and
        not (IsUninstaller and (CompareText(Path, ExpandConstant('{uninstallexe}')) = 0)) then begin
        Result := '설치 폴더의 앱 또는 서버가 실행 중입니다. 진행 작업을 확인하고 정상 종료한 뒤 다시 시도하세요.';
        Exit;
      end;
    end;
  except
    Result := '실행 중인 앱을 확인할 수 없습니다. Windows 관리 서비스 상태를 확인한 뒤 다시 시도하세요.';
  end;
end;

procedure InitializeWizard;
begin
  ServerMode := CreateInputOptionPage(wpSelectDir, '서버 연결', '이 PC에서 사용할 서버를 선택하세요.',
    '앱, 제어 서버와 영상 서버를 설치합니다. 계정과 장비 데이터는 설치 파일에 포함하지 않습니다.', True, False);
  ServerMode.Add('기존 설정 유지 / 원격 서버 사용 / 새 서버는 설치 후 설정');
  ServerMode.Add('이 PC의 기존 서버 데이터 폴더 연결');
  ServerMode.SelectedValueIndex := 0;
  if ExpandConstant('{param:CONNECTLOCAL|0}') = '1' then ServerMode.SelectedValueIndex := 1;
  DataPage := CreateInputDirPage(ServerMode.ID, '기존 제어 서버 데이터', '이미 사용 중인 데이터 폴더를 선택하세요.',
    'host.json과 control.sqlite가 있는 폴더를 선택합니다. 데이터는 복사하거나 초기화하지 않습니다. 새 서버는 설치 안내의 최초 설정 절차를 사용하세요.', False, '');
  DataPage.Add('기존 데이터 폴더:');
  DataPage.Values[0] := ExpandConstant('{param:HOSTDATA|}');
  MediaPage := CreateInputFilePage(DataPage.ID, '기존 영상 서버 설정', '사용할 MediaMTX 설정 파일을 선택하세요.',
    '기존 API/HLS 계정과 포트가 설정된 파일을 선택하세요. 비밀번호를 변경하거나 복사하지 않습니다. 영상 서버 실행 파일은 이번 설치본을 사용합니다.');
  MediaPage.Add('MediaMTX 설정 파일:', 'YAML 파일|*.yml;*.yaml|모든 파일|*.*', '.yml');
  MediaPage.Values[0] := ExpandConstant('{param:MEDIACONFIG|}');
  ApiPage := CreateInputQueryPage(MediaPage.ID, '영상 서버 API 포트', '선택한 설정 파일의 API 포트를 입력하세요.',
    '같은 PC의 loopback API를 사용합니다. mediamtx.yml의 apiAddress 포트와 일치해야 합니다.');
  ApiPage.Add('API 포트:', False);
  ApiPage.Values[0] := ExpandConstant('{param:MEDIAPORT|9997}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = DataPage.ID) or (PageID = MediaPage.ID) or (PageID = ApiPage.ID)) and
    (ServerMode.SelectedValueIndex <> 1);
end;

function ValidateConfiguration: String;
var
  Port: Integer;
begin
  Result := '';
  if ServerMode.SelectedValueIndex <> 1 then Exit;
  if not FileExists(AddBackslash(DataPage.Values[0]) + 'host.json') or
    not FileExists(AddBackslash(DataPage.Values[0]) + 'control.sqlite') then
    Result := 'host.json과 control.sqlite가 있는 기존 데이터 폴더를 선택하세요.'
  else if (ExtractFileDrive(DataPage.Values[0]) = '') or (Pos('\\', DataPage.Values[0]) = 1) then
    Result := '데이터 폴더는 이 PC의 로컬 절대 경로여야 합니다.'
  else if UnderPath(DataPage.Values[0], WizardDirValue) or UnderPath(WizardDirValue, DataPage.Values[0]) then
    Result := '설치 폴더와 운영 데이터 폴더는 서로 겹치지 않는 별도 위치를 선택하세요.'
  else if not FileExists(MediaPage.Values[0]) or (ExtractFileDrive(MediaPage.Values[0]) = '') or
    (Pos('\\', MediaPage.Values[0]) = 1) then
    Result := '기존 MediaMTX 설정 파일의 로컬 절대 경로를 선택하세요.'
  else if UnderPath(MediaPage.Values[0], WizardDirValue) then
    Result := '영상 서버 설정 파일은 설치 폴더 밖에 보관하세요.';
  Port := StrToIntDef(ApiPage.Values[0], 0);
  if (Result = '') and ((Port < 1024) or (Port > 65535)) then
    Result := 'API 포트는 1024~65535 범위여야 합니다.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := ValidateConfiguration;
  if Result = '' then Result := RunningInstallation;
end;

function JsonString(Value: String): String;
begin
  StringChangeEx(Value, '\', '\\', True);
  StringChangeEx(Value, '"', '\"', True);
  Result := '"' + Value + '"';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Settings, SettingsPath: String;
begin
  if (CurStep = ssPostInstall) and (ServerMode.SelectedValueIndex = 1) then begin
    SettingsPath := ExpandConstant('{app}\App\server-startup.json');
    Settings := '{' + #13#10 + '  "enabled": true,' + #13#10 +
      '  "hostDataPath": ' + JsonString(DataPage.Values[0]) + ',' + #13#10 +
      '  "controlHostExecutablePath": "../ControlHost/IntegratedContro.ControlHost.exe",' + #13#10 +
      '  "mediaMtxExecutablePath": ' + JsonString(ExpandConstant('{app}\MediaMTX\mediamtx.exe')) + ',' + #13#10 +
      '  "mediaMtxConfigurationPath": ' + JsonString(MediaPage.Values[0]) + ',' + #13#10 +
      '  "mediaMtxApiEndpoint": "http://127.0.0.1:' + IntToStr(StrToInt(ApiPage.Values[0])) + '"' + #13#10 + '}';
    if FileExists(SettingsPath) then
      if not FileCopy(SettingsPath, SettingsPath + '.previous', False) then
        RaiseException('기존 서버 시작 설정을 백업할 수 없습니다.');
    if not SaveStringToFile(SettingsPath, UTF8Encode(Settings), False) then
      RaiseException('서버 시작 설정을 저장할 수 없습니다.');
  end;
end;

function InitializeUninstall: Boolean;
var
  Problem: String;
begin
  Problem := RunningInstallation;
  Result := Problem = '';
  if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;

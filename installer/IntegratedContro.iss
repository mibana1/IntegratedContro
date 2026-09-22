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
DisableDirPage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Messages]
FinishedLabel=IntegratedContro 설치가 완료되었습니다.%n%n앱을 실행하면 초기 설정에서 새로 시작, 기존 서버 접속 또는 기존 데이터 사용을 선택할 수 있습니다. 저장된 접속 설정이 있으면 로그인 화면으로 이어집니다.%n%n데이터 연결이나 저장 위치 변경은 앱의 '초기 설정 · 저장 위치'에서 진행합니다. 다시 설치할 필요가 없습니다.

[Tasks]
Name: "startmenuicon"; Description: "시작 메뉴에 바로가기 만들기"; GroupDescription: "바로가기:"
Name: "desktopicon"; Description: "바탕 화면에 바로가기 만들기"; GroupDescription: "바로가기:"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\IntegratedContro"; Filename: "{app}\App\IntegratedContro.App.exe"; WorkingDir: "{app}\App"; Tasks: startmenuicon
Name: "{group}\설치 및 서버 설정 안내"; Filename: "{app}\INSTALL.txt"; Tasks: startmenuicon
Name: "{autodesktop}\IntegratedContro"; Filename: "{app}\App\IntegratedContro.App.exe"; WorkingDir: "{app}\App"; Tasks: desktopicon

[Run]
Filename: "{app}\App\IntegratedContro.App.exe"; Description: "IntegratedContro 실행"; WorkingDir: "{app}\App"; Flags: nowait postinstall skipifsilent

[Code]
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

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := RunningInstallation;
end;

function InitializeUninstall: Boolean;
var
  Problem: String;
begin
  Problem := RunningInstallation;
  Result := Problem = '';
  if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;

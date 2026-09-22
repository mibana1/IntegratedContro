# 설치형 EXE 배포

Windows 11 25H2 Pro·Enterprise / x64용 현재 사용자 설치다. 기본 경로는
`%LOCALAPPDATA%\Programs\IntegratedContro`이며 설치 화면에서 바꿀 수 있다.
관리자 권한을 요청하지 않고, 한국어 설치 화면·선택 가능한 시작 메뉴/바탕 화면 바로가기·Windows 앱 제거 항목을 제공한다.

## 사용자 설치

`IntegratedContro-Setup-<빌드 시각>.exe` 하나를 전달한다. App·ControlHost·.NET 런타임·LibVLC와 MediaMTX 1.21.0을
포함하므로 설치 중 다운로드가 없다. 자세한 새 서버 최초 설정과 기존 서버 연결 방법은
[동봉 설치 안내](../installer/INSTALL.txt)를 따른다. 서버를 이미 운영하는 PC는 기존 Windows 계정으로 설치한다.

- 설치 프로그램에서는 **설치 위치·시작 메뉴/바탕 화면 바로가기**만 설정한다. 서버 연결·데이터 폴더·MediaMTX 설정은 앱에서 진행한다.
- 완료 화면의 **IntegratedContro 실행**은 기본 선택되어 있다. 첫 실행에는 **새로 시작 / 기존 서버 접속 / 기존 데이터 사용**을 선택하며, 저장된 접속 설정이 있으면 로그인으로 이어진다.
  실행을 해제한 경우에는 나중에 바로가기 또는 설치 폴더의 App/IntegratedContro.App.exe로 실행한다. 무인 설치는 앱을 실행하지 않는다.
- 데이터 연결이나 위치 변경은 로그인 화면의 **초기 설정 · 저장 위치**에서 진행한다. 이를 위해 재설치할 필요가 없다.
  기존 폴더의 DB·인증서 검증, 경로 변경과 오류 복구는 [초기 설정 안내](INITIAL_SETUP.md)를 따른다.
- 설치 프로그램은 사용자 설정을 생성하거나 덮어쓰지 않는다. 앱이 %LOCALAPPDATA%\IntegratedContro의 client.json·server-startup.json을 관리하며 업데이트·제거에도 유지한다.
  과거 App/server-startup.json은 사용자 설정이 없을 때 앱에서 한 번 가져온다.
- 설치 프로그램의 과거 연결 인수(/CONNECTLOCAL, /HOSTDATA, /MEDIACONFIG, /MEDIAPORT, /PROFILEDIR)는 더 이상 사용하지 않는다. 설치 후 앱에서 연결을 저장한다.
- 기존 DB, 계정, 인증서 개인 키, DPAPI 파일, 현장 MediaMTX 비밀번호는 설치물에 포함하지 않는다.
  새 서버의 계정 초기 설정은 앱에서 실행하며 기존 ControlHost setup 명령도 유지한다.
- 설치/제거는 실행 중인 해당 설치 폴더의 앱·서버를 종료하지 않고 작업을 중단한다.
  기존 배포 폴더의 서버는 그대로 두므로 사용자가 작업을 확인하고 정상 종료한 뒤 새 서버로 전환한다.
- 제거 시 제품 파일과 바로가기만 제거하며, 운영 데이터·사용자 프로필·사용자가 만든 서버 시작 설정은 보존한다.
- 서비스, 부팅 자동 실행, 방화벽 규칙은 설치하지 않는다. 다른 PC·Windows 계정으로 보호 데이터 이전은 별도 검증이 필요하다.

## 빌드

PowerShell 7에서 저장소 루트를 기준으로 실행한다. SDK 10.0.401과 런타임 10.0.12를 사용한다. 검증·배포 스크립트는 scripts/use-dotnet.ps1로 설치된 SDK 또는 artifacts/dotnet-sdk-10.0.401의 휴대형 SDK를 선택한다.

~~~powershell
# 검증을 마친 최신 기존 배포본을 설치 EXE로 묶기
.\scripts\build-installer.ps1

# 새 App/ControlHost 배포와 바로가기 갱신 후 설치 EXE도 만들기
.\scripts\publish.ps1 -Installer
~~~

결과는 artifacts/installer/IntegratedContro-Setup-*.exe와 installer-result.json에 생성된다.
JSON에는 원본 빌드 이름, 크기, SHA-256과 생성 시각을 기록한다. 개발 작업 공간 바로가기와
최신 배포 3개 보관 정책은 기존 publish.ps1을 따른다. 기존 빌드 패키징만으로 앱을 다시 빌드하거나 바로가기를 바꾸지 않는다.

Inno Setup 7.1.0의 공식 서명·고정 SHA-256을 검사하고 artifacts/installer-tools에 휴대형으로 준비한다.
처음 도구/MediaMTX 아카이브를 준비할 때만 네트워크가 필요하다. 이후 캐시를 사용한다.
MediaMTX는 공식 1.21.0 아카이브의 고정 해시를 검증하고 EXE와 MIT LICENSE만 추출한다.
FFmpeg와 테스트/운영 설정은 넣지 않는다. LibVLC는 교체 가능한 동적 라이브러리 구조로 설치한다.

빌드 원본에서 PDB·설치 PC별 server-startup.json을 제외하고 운영 파일 패턴과 재분석 지점을 거부한다.
동봉 package-manifest.json은 설치될 모든 제품 파일의 상대 경로·크기·SHA-256을 담는다.
제품 서명 인증서를 지정하지 않았으므로 현재 생성 EXE는 Authenticode 서명 없는 개발 배포본이다.

## 검증

현재 실행 결과와 설치 EXE 식별자는 [배포 검증 기록](RELEASE_VALIDATION.md)을 따른다.

tests/InstallerSmoke.ps1은 설치된 ControlHost·MediaMTX와 앱의 초기 설정 화면을 사용한다. 먼저 전체 검증을 실행한 후 같은 배포본으로 제품 설치물과 별도 AppId의 검증 설치물을 만든다.

~~~powershell
.\scripts\verify.ps1 -IncludeMediaSmoke
.\scripts\build-installer.ps1 -VerificationPackage -OutputDirectory "$PWD\artifacts\installer-verification"
.\tests\InstallerSmoke.ps1 -InstallerPath "$PWD\artifacts\installer-verification\IntegratedContro-Setup-<빌드 시각>-verification.exe"
~~~

테스트 설치본은 제품 설치본과 같은 스크립트/페이로드에 별도 AppId를 사용한다.
테스트는 artifacts/installer-smoke 아래의 한글·공백 경로, 전용 앱 프로필과 새 호스트 DB에서 실행한다.
바로가기를 만들지 않고 테스트용 제거 등록만 만들었다가 정상 제거한다.
검사는 모든 설치 파일 해시, 설치된 WPF 실행, 앱 설정의 신규·기존·원격 연결·서버 실행 거절·접속 실패·손상 설정을 확인한다.
MediaMTX 자동 설정·재시도·외부 파일 보존, 비밀번호 변경·파일 일부 저장 실패·호스트 재시작·이전 설정 복구와 API/HLS 재인증도 포함한다.
실행 중 앱/호스트의 업데이트·제거 차단, 재설치/제거 후 DB·인증서·보호 파일·프로필의 해시 보존을 확인한다.
이전 검증 EXE를 -PreviousInstallerPath로 주면 이전 버전에서 만든 데이터·계정을 새 버전 설치 직후에도 대조하고 새 호스트로 다시 연다. 영상 실패 유도는 신규 설치 경로에서 수행하며, 이전 버전 경로에서는 첫 실행·업데이트·기존 데이터 재열기·재설치·제거 보존에 집중한다. 실제 두 PC·Enterprise·설치 마법사 물리 입력 검수는 별도다.
실패한 테스트 설치의 등록이 남으면 해당 테스트 경로를 확인하고 그 설치의 unins000.exe로 제거한 뒤 다시 실행한다.

운영 DB·현장 장비에는 테스트 명령을 보내지 않는다. 깨끗한 다른 PC와 Enterprise의 설치 검수는 별도다.

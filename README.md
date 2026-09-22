# IntegratedContro

Windows 11 25H2 Pro·Enterprise / x64용 통합 제어 앱입니다.
일반 장비는 가상 모델이며 Hiperwall LIVE 편집과 카메라 영상 연동을 제공합니다.
WPF 운영 앱과 HTTPS 실행 호스트가 별도 프로세스로 동작합니다.

- 앱에서 최초 관리자 입력으로 데이터 폴더·DB·인증서 생성 및 접속 정보 자동 저장, 관리자의 운영자·조회 계정 등록
- 장비·역할·순차 시나리오 설정, 가상 상태·요청값·단계별 결과 표시
- 시나리오의 저장 배치 표시·조건 충족까지 대기, 시작 당시 배치 고정·대상 예약·교대 후 취소
- 시작 접속 팝업, 관리자 전용 설정 탭, 한국어 감사 기록
- 조명 ON/OFF 카드, 전체·그룹 ON/OFF, 마우스·터치 드래그 배치와 공유 저장
- 한 앱 세션의 배타적 사용권, 교대 후 이전 사용자 작업 보존·선택 취소
- 불변 실행 snapshot, 대상 예약, 명시적 시나리오 중단 및 가상 상태 대조 후 수동 전환
- SQLite 복구, 오래된 세션 차단, 관리자 복구 인계, 불확실 명령 자동 재전송 금지
- MediaMTX 설정 파일·전용 비밀번호 자동 생성, ControlHost 등록 및 API/HLS 인증 연결 확인
- 카메라 등록·검색·활성화·재동기화·삭제와 실패 경로 정리 큐
- MediaMTX HLS → LibVLC 네이티브 영상, 재연결·음소거·화면 이탈 시 해제
- Hiperwall 저장 배치·수동 표시·미저장 15초 테스트, 소유 인스턴스 추적·호스트 종료/재시작 정리
- Hiperwall JPEG/PNG 프리뷰, 같은 콘텐츠 공유·갱신 실패 표시·메모리 한도

Hiperwall 기본 조회·추가·변경·닫기의 실제 연동은 사용자가 확인했습니다.
카메라는 생성 영상으로 실제 RTSP·MediaMTX·HLS·네이티브 디코딩을 검증했습니다.
현장 카메라 1대의 RTSP 입력·HLS 전달까지 확인했으며, 현장 카메라 WPF 재생·Controller 이미지 프리뷰·물리 두 PC·Enterprise 검증은 남아 있습니다.
구체적인 범위와 결과는 [영상 검증 기록](docs/MEDIA_VALIDATION.md)을 참고하세요.

## 빌드와 검증

.NET SDK **10.0.400**이 필요합니다. `global.json`, 중앙 패키지 버전과 각 프로젝트의
`packages.lock.json`으로 의존성을 고정합니다.

```powershell
dotnet restore IntegratedContro.sln --locked-mode
dotnet build IntegratedContro.sln --no-restore
.\scripts\verify.ps1
.\scripts\publish.ps1
```

검증 스크립트는 독립된 테스트 데이터와 호스트 프로세스를 생성합니다.
WPF 스모크 검증에는 로그인된 Windows 데스크톱이 필요하며 테스트 창이 잠시 열립니다.
`-SkipUiSmoke`는 UI 실행을 생략합니다. 결과는 `artifacts/test-results`와 `artifacts/ui-smoke`에 저장합니다.
`publish.ps1`은 `artifacts/publish/Build-날짜-시각`에 App/ControlHost를 함께 배포하고,
작업 공간의 `IntegratedContro.lnk` 바로가기를 최신 App으로 갱신합니다.
완성된 빌드는 최신 3개만 보관합니다. 실행 중인 빌드·잠긴 파일·운영 데이터가 발견되면 해당 정리는 보류합니다.
EXE/DLL 복사본과 별도 Current 복사본은 만들지 않습니다.

네이티브 영상 검증은 별도 도구를 준비한 뒤 실행합니다. 생성 영상과 격리된 호스트만 사용하며,
도구는 제품 배포물에 포함하지 않습니다. 처음 준비할 때만 다운로드가 필요합니다.

```powershell
.\scripts\prepare-media-tests.ps1
.\scripts\verify.ps1 -IncludeMediaSmoke
```

LibVLCSharp.WPF 3.10.1과 VideoLAN.LibVLC.Windows 3.0.23.1을 고정했습니다.
MediaMTX 1.21.0이 포함된 배포 구성에서 [초기 설정](docs/INITIAL_SETUP.md)의 자동 생성으로 설정 파일과 전용 계정을 준비합니다.
기존/원격 서버는 [설정 예제](config/mediamtx.example.yml)와 [운영 절차](OPERATIONS.md)의 카메라 항목을 따릅니다.

## 시작

1. 호스트 PC에서 앱을 열고 **초기 설정 · 저장 위치 → 새 로컬 서버 만들기**를 선택합니다.
2. 사용할 빈 로컬 데이터 폴더, 현장 이름, 최초 관리자 아이디·비밀번호를 입력하고 **새 서버 생성 · 설정 저장**을 누릅니다. 앱이 데이터 폴더·DB·인증서를 생성하고 접속 주소·지문을 자동 저장합니다.
3. 서버 실행 질문에서 **예**를 선택한 뒤 생성한 관리자 계정으로 로그인합니다. PowerShell 실행이나 이 PC의 인증서 지문 복사는 필요 없습니다.
4. 관리자가 사용권을 얻어 가상 장비·역할·운영자를 등록합니다.
5. 사용 시작 → 제어 → 사용 종료로 교대합니다. 사용 종료와 앱 종료는 접수 작업을 취소하지 않습니다.

기존 데이터가 있으면 **기존 로컬 데이터 연결 · 위치 변경**을 사용합니다. 다른 PC는 **원격 / 별도 서버 접속**에서 관리자가 제공한 주소·지문을 입력합니다. 자세한 절차는 [초기 설정 안내](docs/INITIAL_SETUP.md)를 따릅니다.

앱은 꺼진 서버의 실행 여부를 묻고 승인한 서버를 시작합니다. 앱이 직접 시작한 서버는 해당 앱의 정상 종료 시 함께 종료하고, 이미 실행 중이던 서버는 유지합니다. Windows 부팅 자동 실행·서비스 등록은 별도입니다.
이전 프로젝트 코드는 작업 공간의 [보관 자료](../archive/README.md)에 있으며 현재 런타임에 포함하지 않습니다.

## 구성

| 프로젝트 | 책임 |
|---|---|
| Core | 값·계약·요청·실행 snapshot |
| Application | 인증·사용권·설정·작업·예약·복구·실행 Worker 로직 |
| Infrastructure | SQLite, 가상 드라이버, 비밀번호 해시, Windows 보호 저장·호환성 진단 |
| ControlHost | 최초 설정 CLI, Kestrel HTTPS, 호스트 실행 수명 |
| App | WPF/XAML·MVVM 운영 화면과 HTTPS 클라이언트 |
| Tests / UiSmoke | 공통 규칙·SQLite·실제 EXE 통합 테스트 / WPF 실행·렌더링 검증 |

[프로젝트 기준](docs/PROJECT_GUIDE.md)과 [문서 목록](docs/README.md)에서 현재 운영·설계·검증 자료를 찾을 수 있습니다.
날짜별 작업 기록은 [구현 이력](docs/history/IMPLEMENTATION_HISTORY.md)에 보관합니다.

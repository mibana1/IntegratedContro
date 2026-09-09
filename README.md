# IntegratedContro

Windows 11 25H2 Pro·Enterprise / x64용 통합 제어 앱의 첫 구현입니다.
현재는 **가상 장비 전용**이며 WPF 운영 앱과 HTTPS 실행 호스트가 별도 프로세스로 동작합니다.

- 호스트 최초 관리자 생성, 관리자의 운영자·조회 계정 등록
- 장비·역할·순차 시나리오 설정, 가상 상태·요청값·단계별 결과 표시
- 조명 ON/OFF 카드, 이름별 그룹, 마우스·터치 드래그 배치와 공유 저장
- 한 앱 세션의 배타적 사용권, 교대 후 이전 사용자 작업 보존·선택 취소
- 불변 실행 snapshot, 대상 예약, 명시적 시나리오 중단 및 가상 상태 대조 후 수동 전환
- SQLite 복구, 오래된 세션 차단, 관리자 복구 인계, 불확실 명령 자동 재전송 금지

실제 두 PC 검증·Enterprise 검증·실장비/영상 연동은 아직 완료하지 않았습니다.

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
`publish.ps1`은 `artifacts/publish/Current/ControlHost`, `artifacts/publish/Current/App`에 win-x64 self-contained 폴더를 만듭니다.

## 시작

1. [운영 절차](OPERATIONS.md)에 따라 호스트 PC에서 **사용자가 선택한 로컬 데이터 폴더**로 `setup`을 한 번 실행합니다.
2. 같은 폴더를 `--data`로 지정해 `ControlHost.exe run`을 실행합니다.
3. 각 PC에서 `IntegratedContro.App.exe`를 실행하고 HTTPS 주소·호스트 SHA-256 지문·앱 계정으로 접속합니다.
4. 관리자가 사용권을 얻어 가상 장비·역할·운영자를 등록합니다.
5. 사용 시작 → 제어 → 사용 종료로 교대합니다. 사용 종료와 앱 종료는 접수 작업을 취소하지 않습니다.

호스트를 자동 실행하거나 Windows 서비스로 등록하지 않습니다. UI가 호스트를 시작/중지하지 않습니다.
`Document/`의 이전 프로젝트 코드는 런타임에 포함하지 않습니다.

## 구성

| 프로젝트 | 책임 |
|---|---|
| Core | 값·계약·요청·실행 snapshot |
| Application | 인증·사용권·설정·작업·예약·복구·실행 Worker 로직 |
| Infrastructure | SQLite, 가상 드라이버, 비밀번호 해시, Windows 보호 저장·호환성 진단 |
| ControlHost | 최초 설정 CLI, Kestrel HTTPS, 호스트 실행 수명 |
| App | WPF/XAML·MVVM 운영 화면과 HTTPS 클라이언트 |
| Tests / UiSmoke | 공통 규칙·SQLite·실제 EXE 통합 테스트 / WPF 실행·렌더링 검증 |

[프로젝트 기준](docs/PROJECT_GUIDE.md), [첫 구현 검증 기록](docs/FIRST_IMPLEMENTATION_VALIDATION.md)을 참고하세요.
기존 사용자 설정에 따라 `docs/`는 Git ignore 대상입니다. 문서 파일은 로컬에서 갱신되었으며 자동으로 stage/commit하지 않았습니다.

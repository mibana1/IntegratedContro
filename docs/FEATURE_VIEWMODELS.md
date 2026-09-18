# 기능별 ViewModel 수정·검증

기능별 편집·선택·명령 상태를 `MainViewModel`에서 독립 인스턴스로 분리했다.
화면은 메인의 속성 위임을 거치지 않고 각 기능 ViewModel을 직접 DataContext로 사용한다.

| 담당 | 파일 | 소유하는 책임 |
|---|---|---|
| 장비 설정·역할 | DeviceSettingsViewModel.cs, DeviceConfigurationViewModel.cs, DeviceConfigurationFields.cs, RoleAssignmentViewModel.cs, RoleManagementViewModel.cs, DeviceRow.cs | 자동 ID·기본 역할, 공유 연결 선택·수정·복제, PC 선택, 선언된 입력·진단, 역할 배정·해제·복구·이름 수정·미배정 삭제 |
| 수동 장비 조작 | `DeviceControlViewModel.cs`, `CommandInputViewModel.cs`, `PowerInputViewModel.cs` | 역할·기능 선택, 값·대기·제한시간 입력·검증, 일반 명령 요청 작성 |
| 계정 관리 | AccountManagementViewModel.cs | 계정 등록·권한 초안, 장비 선택 목록·전체/제한 범위, Hiperwall 정책 표시, 비밀번호 읽기/정리 |
| 작업·교대 | `JobManagementViewModel.cs`, `HandoverViewModel.cs` | 일반/Hiperwall 작업 목록·선택·상세, 이전 세션 표시, 선택 취소·수동 전환·표시 정리 |
| 복구·진단 | `RecoveryViewModel.cs`, `AuditRow.cs` | 감사 목록·선택, 복구 확인 결과·승인 ID·세대, 확인·승인 명령 |
| 조명·장비 슬롯 | `LightingViewModel.cs`, `LightingSlotsViewModel.cs` | 카드 관측값·조작 가능 여부, 그룹·순서 초안, 전원/일괄 요청, 슬롯 선택·이름 초안·저장·복원·삭제·전체 상태 확인 |
| 시나리오 편집 | `ScenarioEditorViewModel.cs` | 저장 목록·선택, 정의 초안·버전, 단계 이동/삭제, 저장·삭제·실행 요청 |
| 시나리오 단계 | `ScenarioViewModel.cs`, `ScenarioDeviceViewModel.cs`, `ScenarioInputViewModel.cs`, `ScenarioSetting.cs` | 단계 종류, 장비별 설정, 독립 대기/제한시간·조건 입력 |
| 기능 공통 계약 | `FeatureViewModel.cs` | `FeatureContext`, 명령 활성화 알림, 형식화된 `ILightingHost`·`IScenarioHost` |
| 앱 연결 | `FeatureHost.cs`, `MainViewModel.cs` | 로그인·사용권·갱신·공통 실행 상태, 오류 표시, HTTP 경로, 접수 불확실 요청 보존·동일 ID 재확인 |
| WPF 입력 | `LightingView.xaml.cs`, `MainWindow.xaml` | 드래그·터치·시각 요소, 기능 ViewModel에 대한 바인딩 |

위 기능 클래스는 `MainViewModel`, `HostClient`, WPF 창·Dispatcher를 참조하지 않는다.
구성 지점에서 형식화된 호스트 계약을 주입하고, 일관된 조회 결과와 권한을 `UpdateContext`로 전달한다.
상태 조회마다 새로운 `StateView`를 전달하며, busy·연결 여부만 바뀌면 같은 조회 결과를 전달할 수 있다.
HTTP 주소나 인증 처리 변경은 `FeatureHost`에서 한다. 조명 상세 이동은 장비 ID 이벤트만 발행한다.
다른 화면의 입력이나 컬렉션에 접근하는 속성을 기능 ViewModel에 추가하지 않는다.
`ManagementContracts.cs`의 장비·계정·작업·복구 계약은 형식화된 요청/응답만 다룬다.
`FeatureHost`는 이 계약과 HTTP 경로를 연결한다. 메인은 로그인·사용권·주기 조회·공통 busy/오류 처리,
접수 결과가 불확실한 요청의 동일 ID 재확인과 화면 간 ID 이벤트 연결을 담당한다.
장비 역할 배정 성공은 역할 ID 이벤트로 수동 조작의 선택에 연결하며, 기능끼리 직접 참조하지 않는다.

수동 조작과 시나리오의 대기시간·제한시간은 별개다. 시나리오 실행은 선택한 저장 정의 ID를 사용하며,
미저장 단계·잘못된 편집 입력이 저장 시나리오 실행값을 바꾸지 않는다.
두 기능의 접수 불확실 요청은 앱의 기존 공통 경로를 사용하므로 서로 덮어쓰지 않는다.
연결 실패와 사용권 상실 시 조작을 차단하고, 같은 세션의 편집 초안과 저장 버전을 유지한다.

## 호스트·화면 없는 단위 검증

저장소 루트에서 실행한다. 실제 제품 소스를 Tests 프로젝트에 링크해 WPF 참조 없이 컴파일하므로,
실수로 메인 화면이나 WPF 타입에 의존하면 이 검증 프로젝트의 빌드도 실패한다.
`FeatureHostFake`는 메모리 상태·요청만 사용하며 계정·SQLite·네트워크·설정 파일을 열지 않는다.

```powershell
dotnet test tests/IntegratedContro.Tests --filter FullyQualifiedName~LightingViewModelTests
dotnet test tests/IntegratedContro.Tests --filter FullyQualifiedName~ScenarioEditorViewModelTests
```

20개 사례로 카드의 대상/PC/버전/관측 고정, 낙관적 전원 표시 방지, 권한·연결·미확정 접수 차단과 알림,
그룹 초안·이동·취소·저장 버전, 인스턴스 간 초안 분리, 정기 갱신 중 입력 보존, 대상 변경 시 조건 초기화,
중복 단계의 위치 기준 이동, 저장 실패 후 명시적 재시도, 실행·삭제 경계를 검증한다.

## 실제 WPF 연결 검증

```powershell
dotnet build IntegratedContro.sln --no-restore
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --lighting-only --profile-dir artifacts/ui-smoke/lighting-profile
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --scenario-editor-only --profile-dir artifacts/ui-smoke/scenario-profile
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --numeric-input-only --profile-dir artifacts/ui-smoke/numeric-profile
```

이 검증은 격리된 프로필·별도 가상 HTTPS 호스트와 실제 WPF 바인딩을 사용한다.
등록·공유 연결·진단·권한 목록은 --device-drivers-only와 DeviceConfigurationViewModelTests로 검증한다.
시나리오 장비별 입력은 `--scenario-settings-only`, 확장 단계는 `--scenarios-only`로 추가 검증한다.
장비 제어 슬롯은 `--light-slots-only`와 `LightSlotTests`로 저장·배치/전원 복원·권한·실패/재시작·초안 보존을 검증한다.
전체 회귀·배포는 기존 `scripts/verify.ps1`, `scripts/publish.ps1`을 따른다.
물리 장비와 물리 포인터 검수를 대신하지 않는다.

## 카메라의 영상벽 콘텐츠 조회 계약

`CameraViewModel`은 `IHiperwallContentLookup`의 `Contents`와 `ConfigurationVersion`만 사용한다.
`MainViewModel`이 이 계약을 구현하는 `HiperwallViewModel`을 주입한다. 카메라가 영상벽의 선택·편집·명령 상태에
접근하는 경로는 두지 않는다. 공용 표시 항목은 `HiperwallItemRow.cs`에 있다.

조회 구현은 인스턴스 수명 동안 같은 `ReadOnlyObservableCollection<HiperwallItemRow>`을 반환하고,
UI 스레드에서 현재 목록의 변경 알림을 전달해야 한다. 화면 필터와 무관한 전체 Contents를 제공하며,
조회 무효화·설정 변경·세션 종료 때 기존 목록을 비운다. 구성 버전은 저장 요청을 만드는 시점에 읽는다.
현재 구현은 영상벽의 원본 컬렉션을 읽기 전용으로 감싸므로 목록 복제나 별도 이벤트 구독 수명이 없다.
호스트의 매핑 유효성·버전 검증, HTTP/DB 계약은 기존 규칙을 따른다.

```powershell
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --camera-content-lookup-only --profile-dir artifacts/ui-smoke/content-lookup-profile
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --environment-adapters-only --profile-dir artifacts/ui-smoke/content-lookup-engine-profile
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --draft-recovery-only --profile-dir artifacts/ui-smoke/content-lookup-draft-profile
```

가짜 조회 구현으로 목록 변경 알림, 읽기 전용 접근, 중복 이름의 UUID 선택, 원문 전체 이름,
수정 시 매핑 유지·명시적 해제, 오래된 설정 버전 거절·최신 버전 저장을 검증한다.
저장 검증은 격리된 HTTPS 호스트와 가짜 Controller·MediaMTX를 사용한다.
전체 WPF 회귀에는 실제 영상벽 공급자의 목록 갱신·필터 독립성·무효화·설정 변경·로그아웃도 포함한다.


## 장비·계정·작업·복구 상태 수명

- 같은 세션의 폴링·연결 실패·사용권 반납에서는 장비·계정 초안과 수동 입력을 유지한다.
  장비 저장은 편집 시 읽은 PC/장비 ID·설정 버전을 사용하며, 조회된 새 버전으로 저장 기준을 바꾸지 않는다.
  장비 목록의 행 객체와 선택을 유지해 열린 목록·입력 포커스를 보존한다.
- 로그아웃·인증 만료·다른 세션에서는 각 기능이 자신의 선택과 초안을 초기화한다.
  장비 기본 PC ID는 앱 설정에서 전달받고, 다른 PC로 저장한 입력은 해당 세션의 초안에만 남는다.
  계정 비밀번호는 필드로 저장하지 않고 작업의 `finally`와 세션 종료·창 종료 콜백으로 비운다.
- 계정·장비 설정은 관리자 사용권, 작업 취소·수동 조작은 현재 사용권을 요구한다.
  실제 권한과 대상 검증은 호스트가 담당한다. 모든 기능 명령은 같은 앱 busy·오류·상태 갱신 경계를 거친다.
- 복구 승인은 같은 세션·사용권 세대의 확인 결과가 있어야 활성화된다.
  연결 단절·세대/세션 변경·창 종료 때 확인 결과를 폐기하고 다시 확인해야 한다.
  이전 세션에서 늦게 도착한 확인 결과는 새 세션에 적용하지 않는다.
- 작업 목록은 원 요청자·원 세션을 유지하고 취소 요청에는 현재 사용권 세대를 전달한다.
  일반 작업 취소, 시나리오 수동 전환, Hiperwall 미전송 취소와 표시 정리는 각각의 기존 호스트 계약을 사용한다.

```powershell
dotnet test tests/IntegratedContro.Tests --filter FullyQualifiedName~ManagementViewModelTests
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --management-only --profile-dir artifacts/ui-smoke/management-profile
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --device-drivers-only --profile-dir artifacts/ui-smoke/management-device-profile
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --role-unassignment-only --profile-dir artifacts/ui-smoke/management-role-profile
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --handover-only --profile-dir artifacts/ui-smoke/management-handover-profile
```

새 기능 소스를 WPF·MainViewModel 없이 Tests 프로젝트에 직접 연결해 구조 경계를 컴파일로 검증한다.
`ManagementViewModelTests`는 가짜 형식화 호스트로 초안·선택·버전·권한·암호 정리·취소 대상·복구 수명을 확인한다.
`--management-only`는 격리된 HTTPS 호스트에서 실제 계정 편집 폼과 heartbeat 단절 → 복구 확인 → 승인 흐름을 확인한다.

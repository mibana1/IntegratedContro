# 기능별 ViewModel 수정·검증

조명과 시나리오 편집은 `MainViewModel`의 partial 구현에서 독립 인스턴스로 분리했다.
화면 바인딩은 각각 `MainViewModel.Lighting`, `MainViewModel.ScenarioEditor`를 사용한다.

| 담당 | 파일 | 소유하는 책임 |
|---|---|---|
| 조명 | `src/IntegratedContro.App/LightingViewModel.cs` | 카드 관측값·조작 가능 여부, 그룹·순서 초안, 대상과 관측을 고정한 전원/일괄 요청 |
| 시나리오 편집 | `ScenarioEditorViewModel.cs` | 저장 목록·선택, 정의 초안·버전, 단계 이동/삭제, 저장·삭제·실행 요청 |
| 시나리오 단계 | `ScenarioViewModel.cs`, `ScenarioDeviceViewModel.cs`, `ScenarioInputViewModel.cs`, `ScenarioSetting.cs` | 단계 종류, 장비별 설정, 독립 대기/제한시간·조건 입력 |
| 기능 공통 계약 | `FeatureViewModel.cs` | `FeatureContext`, 명령 활성화 알림, 형식화된 `ILightingHost`·`IScenarioHost` |
| 앱 연결 | `FeatureHost.cs`, `MainViewModel.cs` | 로그인·사용권·갱신·공통 실행 상태, 오류 표시, HTTP 경로, 접수 불확실 요청 보존·동일 ID 재확인 |
| WPF 입력 | `LightingView.xaml.cs`, `MainWindow.xaml` | 드래그·터치·시각 요소, 기능 ViewModel에 대한 바인딩 |

조명과 시나리오 클래스는 `MainViewModel`, `HostClient`, WPF 창·Dispatcher를 참조하지 않는다.
구성 지점에서 형식화된 호스트 계약을 주입하고, 일관된 조회 결과와 권한을 `UpdateContext`로 전달한다.
상태 조회마다 새로운 `StateView`를 전달하며, busy·연결 여부만 바뀌면 같은 조회 결과를 전달할 수 있다.
HTTP 주소나 인증 처리 변경은 `FeatureHost`에서 한다. 조명 상세 이동은 장비 ID 이벤트만 발행한다.
다른 화면의 입력이나 컬렉션에 접근하는 속성을 기능 ViewModel에 추가하지 않는다.

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
시나리오 장비별 입력은 `--scenario-settings-only`, 확장 단계는 `--scenarios-only`로 추가 검증한다.
전체 회귀·배포는 기존 `scripts/verify.ps1`, `scripts/publish.ps1`을 따른다.
물리 장비와 물리 포인터 검수를 대신하지 않는다.

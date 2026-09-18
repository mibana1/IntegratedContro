# OS·영상 엔진·저장소 어댑터 책임

환경 구현을 교체할 때 업무 서비스나 카메라 화면에서 구체 구현을 선택하지 않는다.
호스트의 환경 어댑터 선택은 [HostAdapters.cs](../src/IntegratedContro.ControlHost/HostAdapters.cs),
클라이언트 영상 선택은 [AppAdapters.cs](../src/IntegratedContro.App/AppAdapters.cs)에 둔다.

## 변경별 수정 위치

| 변경 | 주 수정 위치 | 유지할 경계 |
|---|---|---|
| 공식 지원 Windows 버전·에디션·아키텍처 | Application/[PlatformPolicy.cs](../src/IntegratedContro.Application/PlatformPolicy.cs) | 전달받은 환경 값만 판정. 레지스트리·설치·장치 초기화를 호출하지 않음 |
| 실제 OS 정보 조회 방식 | Infrastructure/Platform/[WindowsEnvironment.cs](../src/IntegratedContro.Infrastructure/Platform/WindowsEnvironment.cs) | `IPlatformEnvironment`로 값만 반환. 조회 실패를 공식 지원으로 처리하지 않음 |
| DPAPI 범위·대체 보호 방식 | Infrastructure/Platform/[WindowsCurrentUserSecretProtector.cs](../src/IntegratedContro.Infrastructure/Platform/WindowsCurrentUserSecretProtector.cs), HostAdapters | `ISecretProtector` 구현 선택. 평문 저장이나 다른 계정으로 자동 대체하지 않음 |
| 인증서·Windows TLS 키 로드 | Infrastructure/Platform/[HostCertificate.cs](../src/IntegratedContro.Infrastructure/Platform/HostCertificate.cs) | 인증서 생성·Schannel 키 로드는 OS 경계. 암호화는 주입된 보호 어댑터 사용 |
| 호스트 데이터 경로 허용 조건 | Infrastructure/Platform/[LocalHostDataPath.cs](../src/IntegratedContro.Infrastructure/Platform/LocalHostDataPath.cs) | 명시적인 고정 로컬 절대 경로, 공유·재분석 지점 거절, 기존 폴더 접근 실패 시 대체 DB 생성 금지 |
| 클라이언트 프로필 경로·프로세스 잠금·Windows 공유 위반 재시도 | App/[ClientProfileEnvironment.cs](../src/IntegratedContro.App/ClientProfileEnvironment.cs) | `%LOCALAPPDATA%`, `--profile-dir`, 이름 있는 mutex와 Windows 오류 코드 처리. JSON 검증·백업 복구 정책은 ClientPreferences |
| 앱 시작 시 서버 확인·실행 | App/[LocalServerStartup.cs](../src/IntegratedContro.App/LocalServerStartup.cs), [WindowsServerProcesses.cs](../src/IntegratedContro.App/WindowsServerProcesses.cs) | 꺼진 서버만 확인 후 시작, TCP 포트 소유·별도 프로세스·시작 직렬화는 Windows 경계. 앱이 시작한 프로세스만 LocalServerLifetime이 소유하여 UI 정상 종료 시 종료. ControlHost AppOwnerLifetime의 종료 신호로 기존 정리 절차 실행. 기존 서버와 다른 DB는 건드리지 않음 |
| 호스트 저장 엔진 선택·연결 구성 | Infrastructure/[HostStorage.cs](../src/IntegratedContro.Infrastructure/HostStorage.cs), HostAdapters | `IStateStore`와 `IVirtualDeviceTransport`를 같은 운영 저장소에 구성. SQL 연결 문자열을 진입점/드라이버로 전달하지 않음 |
| SQLite 스키마·트랜잭션·가상 값 SQL | Infrastructure/[SqliteStateStore.cs](../src/IntegratedContro.Infrastructure/SqliteStateStore.cs), [SqliteVirtualDeviceTransport.cs](../src/IntegratedContro.Infrastructure/SqliteVirtualDeviceTransport.cs) | 저장 실패 시 접수 완료로 응답하지 않음. 호스트 단일 소유·재시작 복구·전송 전 영속화 유지 |
| 자격 증명 파일명·저장·읽기·삭제 | Infrastructure/[HiperwallCredentialStore.cs](../src/IntegratedContro.Infrastructure/HiperwallCredentialStore.cs), [MediaCredentialStore.cs](../src/IntegratedContro.Infrastructure/MediaCredentialStore.cs) | 파일 저장과 오류 계약만 소유. 직접 DPAPI를 호출하지 않음 |
| 네이티브 영상 엔진·코덱·GPU·재연결 | App/Video/[VlcVideoPlayer.cs](../src/IntegratedContro.App/Video/VlcVideoPlayer.cs), AppAdapters | Core `IVideoPlayer`로 상태·재생·정지·음소거·해제만 전달 |
| 엔진의 WPF 표시 표면·UI 스레드 수명 | App/Video/[VlcVideoPresentation.cs](../src/IntegratedContro.App/Video/VlcVideoPresentation.cs) | App 전용 `IVideoPresentation`. 네이티브 타입은 ViewModel/XAML/Core/Application에 노출하지 않음 |
| 영상 호스트 인증·루프백 중계 | App/Video/[LoopbackVideoRelay.cs](../src/IntegratedContro.App/Video/LoopbackVideoRelay.cs) | 카메라 원문 인증 정보를 엔진 URL이나 파일에 넣지 않음 |
| 엔진 패키지·아키텍처·배포 파일 | [IntegratedContro.App.csproj](../src/IntegratedContro.App/IntegratedContro.App.csproj), [Directory.Packages.props](../Directory.Packages.props), 잠금 파일·[배포 스크립트](../scripts/publish.ps1) | 어댑터 변경에 맞춰 런타임·네이티브 파일·라이선스와 실제 배포본 재생을 검증 |

## 실행 경계

호스트 `setup`과 `run`은 같은 HostAdapters를 거친다. `HostStorage.State`는 Application에 전달하고
`HostStorage.VirtualDevices`는 가상 드라이버에 전달한다. 가상 드라이버는 저장 엔진을 생성하지 않는다.
호스트의 저장소 수명이 끝나면 DB와 단일 실행 잠금을 해제한다.

카메라 ViewModel은 주입된 팩터리로 IVideoPresentation을 받아 재생을 요청한다.
WPF 화면은 ContentControl로 어댑터가 제공한 표면을 표시한다.
LibVLC 생성은 작업 스레드에서, VideoView 생성·분리는 WPF Dispatcher에서 수행한다.
정지·화면 숨김·로그아웃은 콜백 해제 → 루프백 요청 종료 → 표시 표면·엔진 해제 순서로 정리한다.
초기화가 늦게 완료되어도 이미 숨겨진 화면에서는 재생하지 않는다. 초기화 실패 뒤에는 사용자가 다시 재생할 수 있다.

Core/Application은 WPF·Win32·COM·레지스트리·SQLite·LibVLC 구현을 참조하지 않는다.
`EnvironmentBoundaryTests`가 공통 어셈블리/소스와 카메라·호스트 진입점 경계를 검사한다.
다른 엔진도 `IVideoPlayer.PlayAsync`의 작업으로 재생 수명을 표현하고 취소·Dispose를 완료해야 한다.
App 전용 Surface는 WPF 표시 객체이며 공통 서비스·호스트 API로 보내지 않는다.

## 호환성과 확장

- 이번 변경은 책임 분리다. 지원 OS는 Windows 11 25H2 Pro·Enterprise / OS와 프로세스 모두 x64다.
  ARM64의 x64 에뮬레이션, Server, 미래 버전을 공식 지원으로 오인하지 않는다.
  OS 지원 판정은 장치·영상 기능의 초기화 성공이나 현장 검증을 의미하지 않는다.
- 운영 SQLite 파일·스키마·호스트 API, client.json 내용과 PC ID, 자격 증명 파일명과
  DPAPI CurrentUser/null entropy, 인증서 UserKeySet 로드 방식은 유지한다. 데이터 마이그레이션은 없다.
- 보호 방식·Windows 실행 계정·저장 엔진을 실제로 바꿀 때는 기존 참조 ID·암호문·접수 작업의 이관을 별도로 구현한다.
  어댑터 클래스만 교체하면 기존 데이터를 읽을 수 있다고 가정하지 않는다.
- 현재 구현은 Windows/SQLite/LibVLC다. 다른 OS·DB·영상 엔진 구현이나 동적 플러그인 탐색을 추가한 것은 아니다.
- PC 장치 어댑터를 추가할 때도 실제 PC ID·장치 ID를 확인한다. 미지원이나 연결 실패를 로컬 PC 제어로 대체하지 않는다.

## 검증

```powershell
dotnet test tests/IntegratedContro.Tests --filter "FullyQualifiedName~Environment"
dotnet run --project tests/IntegratedContro.UiSmoke -- --environment-adapters-only --profile-dir artifacts/ui-smoke/environment-profile
./scripts/verify.ps1 -IncludeMediaSmoke
```

- 환경 테스트: 지원/미지원/조회 실패 정책, 보호 어댑터 주입·실패·평문 버퍼 정리,
  기존 DPAPI 파일 읽기와 동일 형식 신규 저장, 같은 DB의 상태·가상 값·실행 잠금 유지.
- 엔진 없는 WPF 검증: 대체 엔진 선택, 음소거, 정지, 초기화 실패 후 재시도,
  동기/비동기 재생 실패, 콜백 해제, 화면 숨김 중 늦은 초기화 정리.
- 기존 통합/WPF 회귀: 계정·사용권·접수 snapshot·작업/시나리오 보존·복구, 프로필 복구, 카메라/영상벽.
- 네이티브 미디어 검증: 생성한 루프백 RTSP → MediaMTX → 인증 HLS → LibVLC 재생,
  재연결·음소거·목록 갱신·최소화·탭 이탈·로그아웃. 현장 카메라 검수와 구분한다.

OS/엔진/저장소를 교체한 경우 해당 환경에서 실제 설치·재시작·권한·데이터 복구를 추가 검증한다.
배포는 scripts/publish.ps1을 사용하고 기존 실행 중 프로세스는 자동 종료하지 않는다.

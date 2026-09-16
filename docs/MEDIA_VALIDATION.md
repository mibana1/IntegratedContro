# 카메라·MediaMTX·영상·프리뷰 검증

검증일: 2026-09-14. 실제 운영 호스트/카메라를 수정하지 않고 격리 데이터·동적 loopback 포트·로컬 생성 영상을 사용했다.
Hiperwall 기본 조회·추가·변경·닫기의 실제 연동은 사용자가 별도로 확인했다.

## 2026-09-15 등록 입력 포커스 수정

2초 주기 카메라 목록 자동 갱신이 일반 명령과 같은 busy 상태로 입력 폼을 비활성화했다.
그 순간 TextBox/PasswordBox의 키보드 포커스가 빠져 연속 입력을 중단시켰다.
자동 조회와 직접 실행한 명령을 구분해 자동 조회 중에는 폼을 활성 상태로 유지한다.
요청 직렬화와 명시적 명령 중 잠금, 권한/사용권 검사는 유지한다.

수정 전 WPF 재현 테스트는 CameraNameInput의 주기 갱신 중 포커스 상실로 실패했다.
수정 후 같은 테스트에서 이름·RTSP 주소·API 주소·HLS 비밀번호에 텍스트 조합 이벤트로
123456789를 입력하고, 각 입력 중 최소 두 번의 자동 조회 완료와 포커스·값·커서 유지를 확인했다.
전체 실행 동안 목록 갱신 13회를 관측했으며 명시적 명령의 입력 잠금,
사용권 반납 시 편집 차단, 로그아웃 시 보호 입력 초기화도 통과했다.
이는 격리 호스트 및 실제 WPF 컨트롤을 사용한 합성 입력 검사이며 물리 키보드 자동화는 아니다.

솔루션 빌드: 경고 0, 오류 0.
결과: artifacts/ui-smoke/camera-input-result.txt.
기본 WPF 스모크 실행에도 해당 검사를 포함했다.

~~~powershell
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --camera-input-only --profile-dir artifacts/ui-smoke/camera-input-profile
~~~

이번 수정 배포본은 App만 생성한다. 경로는 artifacts/publish/camera-input-fix-path.txt에 기록한다.
기존 MediaValidation-20260914-162930의 ControlHost와 현재 MediaMTX를 계속 사용한다.

## 구현 경계

카메라 등록 → 호스트 SQLite 저장 → 별도 MediaMTX 경로 동기화 → 실제 입력 조회 → HLS 읽기 →
WPF의 재생 전용 loopback 중계 → IVideoPlayer/LibVLC 경로다.
경로 준비 완료만으로 실제 영상 수신이나 디코딩 완료를 보고하지 않는다.
카메라 저장은 Hiperwall Source 생성/표시를 수행하지 않는다.

| 계층 | 책임 |
|---|---|
| Core | 카메라·설정·정리 큐·재생 상태 계약, IVideoPlayer, HLS/이미지 제한 |
| Application | 관리자/사용권 검사, 버전 충돌, 저장 우선 동기화, 정확한 삭제 대상, 재시작 복구, 읽기 후 재검증 |
| Infrastructure | MediaMTX v3 API/HLS, Controller JPEG/PNG, DPAPI 비밀 저장 |
| ControlHost | 인증된 카메라 API, 별도 동기화 Worker, 동시 조회 제한 |
| App | 카메라 목록/폼/재생, 인증된 loopback 중계, LibVLC, 보이는 콘텐츠의 프리뷰 캐시 |

API: GET /api/cameras, POST /api/media/settings, POST /api/cameras/save|sync|delete|cleanup,
GET /api/cameras/{id}/status?version=, GET /api/cameras/{id}/hls/{asset}?version=,
POST /api/hiperwall/preview. 쓰기는 관리자+사용권, 읽기는 유효한 로그인과 현재 대상/버전이 필요하다.
HLS와 일반 API의 요청 횟수 제한을 분리해 영상 오류 반복이 생존 확인·로그아웃 요청을 소진하지 않게 한다.

## 실행 결과

| 검증 | 결과 |
|---|---|
| 전체 솔루션 빌드 | 경고 0, 오류 0 |
| 서비스 단위/통합 테스트 | 173/173 통과, 건너뜀 0 |
| WPF 기존 운영·역할·조명·교대·Hiperwall 편집 | 통과, 기존 스모크 및 합성 WPF 입력 검증 포함 |
| 실제 RTSP/HLS/네이티브 영상 | MediaMTX 1.21.0, LibVLC 3.0.23, H.264/AAC, 640×360, 20fps, HLS mpegts |
| 영상 수명 | 초기 음소거·음소거 전환·실제 decoded frame 진행·송출 중단 후 재연결 통과 |
| 화면 수명 | 목록 갱신 중 재생 유지, 최소화/탭 이탈/로그아웃 시 플레이어 해제, 다시 재생 통과 |
| loopback 중계 | 무인증 401, 임의 파일 400, 정상 인증 조회, 대기 중 4개 요청의 취소/해제 통과 |
| 프리뷰 | 2개 동일 콘텐츠 인스턴스의 1회 요청 공유, 기존 이미지/편집 유지와 실패 표시, 숨김/로그아웃 캐시 해제 통과 |
| 배포 | 새 MediaValidation-* 폴더에 win-x64 self-contained App/ControlHost 생성, 런타임·x64 VLC·라이선스 파일 확인 |

주요 서비스 회귀 사례:

- 등록을 먼저 저장하고 경로 준비 실패/실제 ready를 분리한다. RTSP/API/HLS 비밀이 DB·일반 응답에 없음을 검사한다.
- 정상 삭제 실패는 등록/실패 상태를 유지하고 재시작만으로 경로를 재생성하지 않는다.
- 강제 삭제와 늦은 동기화가 경합해도 등록이 되살아나지 않는다. 같은 ID로 재등록한 새 경로를 이전 큐가 삭제하지 않는다.
- 비활성/재활성/호스트 재시작, 정리 큐의 시작 즉시 재시도와 원 대상 고정을 검사한다.
- 관리자·조회자·운영자 권한, 사용권, 버전 충돌, 비활성 재생 거부, 로그아웃 후 늦은 HLS 응답 폐기를 검사한다.
- HLS 목록의 외부 주소/경로/키 URI, 응답 크기 초과, 이미지 디코딩 크기 폭증, 인벤토리에 없는 프리뷰 요청을 거부한다.
- MediaMTX 1.21 HLS 쿠키 협상은 정확히 같은 경로의 cookieCheck 리다이렉트만 따르며 다른 원본/경로로 인증을 전달하지 않는다.

## 재현

.NET SDK 10.0.400과 로그인한 Windows x64 데스크톱이 필요하다.
스크립트는 독립 테스트 데이터를 사용한다. 제품은 테스트용 FFmpeg를 포함하지 않는다.

~~~powershell
.\scripts\prepare-media-tests.ps1
.\scripts\verify.ps1 -IncludeMediaSmoke

# 이미 빌드한 상태에서 영상만 재검증
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --media-only --profile-dir artifacts/ui-smoke/media-profile

# 로컬 가짜 Controller의 이미지 프리뷰와 loopback 중계만
dotnet run --project tests/IntegratedContro.UiSmoke --no-build --no-restore -- --preview-only --profile-dir artifacts/ui-smoke/preview-profile
~~~

prepare-media-tests.ps1은 MediaMTX 1.21.0과 FFmpeg 9.0.1 아카이브의 SHA-256을 고정한다.
FFmpeg 배포 사이트의 release URL이 나중에 다른 버전을 제공하면 해시 불일치로 중단한다.
재현에는 artifacts/media-tools에 보관한 검증된 아카이브를 사용하며 새 해시를 자동으로 승인하지 않는다.

증거 파일(로컬 artifacts, Git 제외):

- test-results/camera-regression.trx
- ui-smoke/camera-result.txt, preview-result.txt 및 기존 WPF 결과 파일
- ui-smoke/camera-native-frame.png: 실제 네이티브 디코딩 프레임
- ui-smoke/camera-workspace.png: 카메라 UI 렌더링. WPF RenderTargetBitmap은 자식 HWND 영상을 캡처하지 못하므로 영상은 위 별도 프레임으로 확인한다.
- ui-smoke/wall-preview.png, wall-preview-failure.png
- publish/media-validation-path.txt: 이번에 별도로 생성한 배포본 경로

## 검증 중 수정한 호환성 문제

MediaMTX 1.21의 HLS index는 세션 쿠키 확인을 위해 같은 경로로 302를 돌려준다.
이 제한된 협상만 허용하고, 획득한 세션 쿠키는 호스트 메모리에서 한도/만료를 두어 관리한다.

LibVLC 3의 인증 대화 콜백으로 재생 전용 loopback Basic 자격 증명을 전달하며 저장 옵션은 끈다.
HLS 파일 요청을 반복시키던 http-continuous 옵션은 사용하지 않는다.
ObservableCollection의 목록 갱신 중 선택 초기화가 재생을 중단시키던 문제도 수정했다.

## 남은 현장 확인

- 실제 카메라의 인증 방식·코덱·해상도·키프레임 간격·장시간 지연/메모리/망 단절.
- Controller가 반환하는 실제 JPEG/PNG와 프리뷰 권한/주기.
- 물리 두 PC, Enterprise, 각 PC의 GPU/오디오 드라이버와 표시 배율.
- 현장 MediaMTX의 실행 계정·자동 실행·TLS·로그 보호·보관 정책과 설치 패키지.

제품 검증 범위는 H.264/AAC 및 mpegts HLS다. MediaMTX가 자동 트랜스코딩한다고 가정하지 않는다.
녹화·녹화 파일 재생·영상 서버 자동 설치/시작·WebView2는 구현 범위에 없다.

## 구현 근거

- [MediaMTX Control API](https://mediamtx.org/docs/features/control-api)와 [v1.21.0 API 명세](https://raw.githubusercontent.com/bluenviron/mediamtx/v1.21.0/api/openapi.yaml)
- [MediaMTX v1.21.0 HLS 서버 구현](https://raw.githubusercontent.com/bluenviron/mediamtx/v1.21.0/internal/servers/hls/http_server.go)
- [LibVLCSharp 권장 수명 관리](https://docs.videolan.me/libvlcsharp/docs/best_practices.html)
- [LibVLCSharp.WPF 3.10.1](https://www.nuget.org/packages/LibVLCSharp.WPF/3.10.1), [VideoLAN.LibVLC.Windows 3.0.23.1](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1)
- [영상 구성요소 고지](../THIRD_PARTY_NOTICES.md), [MediaMTX 설정 예제](../config/mediamtx.example.yml)

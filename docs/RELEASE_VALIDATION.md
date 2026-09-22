# 설치·설정·배포 검증 (2026-09-22)

이번 검증은 Windows 11 25H2 Pro x64, 빌드 26200.9457에서 격리된 프로필·DB·포트·설치 폴더를 사용한다.
운영 데이터와 기존 실행 서버는 검증 대상으로 사용하지 않는다. 원격 모드는 같은 PC의 별도 HTTPS 호스트로 확인한다.

## 검증 항목

| 흐름 | 확인 내용 |
|---|---|
| 새로 시작 | 방식 선택, 관리자 생성·즉시 조회 모드 로그인, 비밀번호 입력 지우기, 재실행 시 로그인으로 연결 |
| 기존 데이터 | 호스트 정지 확인, DB·보호 인증서 검사, 기존 현장·계정·감사 이력 유지 |
| 기존 서버 접속 | HTTPS/인증서 지문·계정 로그인, 로컬 생성/시작 비활성화, 앱 종료 후 기존 서버 유지 |
| 설정 중단·실패 | 시작 거절/접속 실패 후 DB·설정 유지, 손상 설정의 신규 초기화 차단, 영상 인증 실패 후 동일 파일·계정 재사용 |
| 영상 비밀번호 | API/HLS 분리, 빈 항목 유지, 비밀번호 원문 비저장, 사용권 없는 변경 차단, API/HLS 인증·이전 비밀번호 거부 |
| 변경 복구 | 실제 파일 잠금으로 부분 적용 실패, DB 복구 기록 유지, 호스트 재시작·다시 적용·이전 설정 복구·재인증 |
| 외부 상태 보존 | 수정된 영상 파일 덮어쓰기 차단, 점유 포트의 기존 프로세스 유지, 앱이 소유한 서버만 종료 |
| 설치·업데이트·제거 | 제품 manifest 해시, 설치된 EXE 실행, 실행 중 차단, 이전 버전 업데이트/재설치/제거 전후 데이터·프로필 해시 보존 |

정식 회귀 코드는 tests/IntegratedContro.UiSmoke의 FirstRunLifecycle, MediaSetupLifecycle, LocalMediaLifecycle 및 SetupLifecycleSmoke에 있다.
기본 verify.ps1은 첫 실행 검사를 포함하며 -IncludeMediaSmoke는 실제 MediaMTX 설정·변경·서버 소유 수명과 생성 영상 검사를 추가한다.
tests/InstallerSmoke.ps1의 신규 설치 경로는 같은 검사에 설치된 ControlHost·MediaMTX 경로를 주입한다. 이전 버전 경로는 두 버전의 첫 실행·설정, 기존 데이터의 새 호스트 재열기, 업데이트·재설치·제거 보존을 검사한다. 영상 실패 유도는 신규 설치와 별도 설치본 검사에서 확인한다. WPF 화면/서비스 검사는 검사 프로젝트가 제품 UI 코드를 참조하며, 설치된 App EXE 시작은 별도로 확인한다.

## 실행 결과와 산출물

- scripts/verify.ps1 -IncludeMediaSmoke 통과: 잠금 복원·배포 정책, Debug 빌드 경고/오류 0, 자동 테스트 428개(실패/건너뜀 0), 전체 WPF 및 첫 실행 6개 경로, 실제 MediaMTX 설정·비밀번호 복구·서버 소유 수명.
- 생성 H.264/AAC 입력의 RTSP → MediaMTX → 인증된 호스트 HLS → LibVLC 디코딩 138프레임, 음소거·입력 중단 후 재연결·최소화·탭 이동·로그아웃 정리를 통과했다.
- 전체 로그: artifacts/verification-net10.0.12-20260922.log. 자동 검사: artifacts/test-results/first-implementation.trx. 영상 결과: artifacts/ui-smoke/camera-result.txt.
- 첫 실행 증거: artifacts/ui-smoke/profile/setup-lifecycle-b18bf50a1849493185dccbb81dbd4d3c.
- 영상 설정·복구 증거: artifacts/ui-smoke/media-profile/setup-lifecycle-fd225040ac0946bb91cc6106055675cd.
- scripts/publish.ps1 -Installer 통과: Build-20260922-143358-797, 최신 App 바로가기 갱신, 최신 3개 보관(실행/잠금 보류 없음).
- 신규 설치 → 설치된 서버를 통한 전체 설정/복구 → 재설치 → 제거 통과. 제품 1,179개 파일의 해시와 데이터·보호 파일·프로필 75개의 보존을 확인했다. 테스트 제거 등록도 정상 정리됐다.
- 신규 설치 증거: artifacts/installer-smoke/c93f8ce4d2b0499cac3720ecf1f45816/result.txt 및 artifacts/installer-clean-20260922.log.
- 파일 복구 검사 보강 후 같은 설치된 서버로 별도 검사를 통과했다. 복구 대기 때 변경 ID·버전 보존을 확인하고 명시적 재시도 1회 후 복구·재인증을 완료했다. 로그: artifacts/local-media-final-verification-20260922.log.
- 전달할 제품과 별도 AppId의 검증 패키지는 1,179개 파일 경로·크기·SHA-256이 모두 같다.
- 이전 버전 2026.9.22.1124 → 새 버전 2026.9.22.1433 업데이트, 새 호스트의 기존 데이터 재열기, 실행 중 업데이트/제거 차단, 재설치·최종 제거를 통과했다. 데이터·보호 파일·프로필 74개의 해시와 제거 등록 정리를 확인했다.
- 업데이트 증거: artifacts/installer-smoke/2b9d43f7c6e74c9a96144ac343db47f6/result.txt 및 artifacts/installer-upgrade-preservation-20260922.log.

설치 파일: artifacts/installer/IntegratedContro-Setup-20260922-143358-797.exe

버전 2026.9.22.1433, 크기 137,551,588바이트.
SHA-256: `06887CC191F2A4912EA60839CD489479AD9BA0D4132DD1F08ADFAD2B1110F2D7`.
빌드 메타데이터는 artifacts/installer/installer-result.json에 있다.

검증 중 외부 데스크톱 창 전환으로 입력 포커스 검사가 중단되어, 창이 실제 비활성화된 경우에만 새 연속 입력 시도를 하도록 검사를 보강했다. 마지막 전체 검사에는 이 재시도가 필요하지 않았다.
비밀번호 검사는 별도 복제 화면 대신 실제 MainWindow의 카메라 탭을 사용하도록 바꿔 화면 수명 경합을 제거했다. 제품의 설정·인증 업무 로직을 변경하지 않았다.
설치본 반복 검사 중 파일 접근 오류로 복구 대기 상태가 남은 경우가 있었다. 같은 설치본의 단독 재검사는 통과했으며 파일 접근 실패의 외부 원인은 특정하지 못했다. 최종 검사는 파일 적용 실패 때 같은 변경 ID·설정 버전을 보존하는지 확인하고 명시적 재적용을 최대 10회 검사하며, 지속 실패는 통과시키지 않는다. 실행 중 인증 변경과 부분 실패 유도를 분리해 영상 서버 정지 후 manifest만 잠그고 YAML 변경·manifest 보존을 직접 대조한다. 검사에서 MediaMTX 수명을 직접 관리하므로 호스트 재시작이 영상 서버를 자동 재실행하지 않는다.

## 빌드 환경과 지원 확인

- SDK 10.0.401, .NET/ASP.NET Core/Windows Desktop Runtime 10.0.12, Microsoft.Data.Sqlite·ProtectedData 10.0.12를 고정했다.
- SDK는 Microsoft 공식 릴리스 메타데이터의 SHA-512를 확인한 Windows x64 ZIP을 artifacts/dotnet-sdk-10.0.401에 풀어 사용한다. 시스템 SDK 설치를 교체하지 않는다. scripts/use-dotnet.ps1로 이 SDK를 선택한다.
- [Microsoft .NET 다운로드](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)의 2026-09-08 보안 패치 10.0.12를 반영했다. [.NET 지원 정책](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)에 따르면 .NET 10 지원 종료는 2028-11-14이며 최신 패치 유지가 필요하다.
- Windows 11 25H2의 [Pro](https://learn.microsoft.com/en-us/lifecycle/products/windows-11-home-and-pro)·[Enterprise](https://learn.microsoft.com/en-us/lifecycle/products/windows-11-enterprise-and-education)가 Microsoft 수명 주기에서 지원 중임을 확인했다. 이 앱의 실제 Enterprise 검수는 별도다.
- MediaMTX 1.21.0, LibVLCSharp.WPF 3.10.1, LibVLC 3.0.23 x64를 유지한다. MediaMTX 배포 아카이브의 고정 SHA-256 및 실제 인증·영상 실행을 검증한다.
- NuGet 의존성 조회 결과는 artifacts/dependency-audit-20260922.log에 보관한다. 현재 NuGet 소스는 직접·간접 패키지의 알려진 취약점을 보고하지 않았다.

## 남은 현장 검수

물리 두 PC의 LAN/방화벽 통신, 깨끗한 별도 PC·Enterprise 설치, 다른 Windows 계정/PC로 DPAPI 이관, 물리 터치·설치 마법사 입력,
현장 카메라의 WPF 재생과 Controller 이미지 프리뷰는 이 로컬 자동 검증으로 완료 처리하지 않는다.
설치 EXE는 제품 Authenticode 서명이 없는 개발 배포본이다.

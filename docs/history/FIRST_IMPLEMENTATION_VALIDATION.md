# 첫 구현 검증 기록

보관 문서: 2026-09-09 당시의 구현·검증 기록이다. 최신 상태는 [프로젝트 기준](../PROJECT_GUIDE.md)을 따른다.
아래 배포 경로·Git 제외 상태·미구현 항목은 당시 기준이며 현재와 다를 수 있다.

기록일: 2026-09-09
범위: 가상 장비 기반 첫 운영 흐름, 로컬 Windows PC에서 수행.
최종 검증 수치는 아래 실행 결과와 artifacts 증거에 대응한다.

## 환경

- Windows 11 25H2 Pro, EditionID Professional, x64
- OS build 26200.9168 (레지스트리 ProductName의 구형 "Windows 10 Pro" 문자열과 구분)
- .NET SDK 10.0.400, .NET/ASP.NET Core/WPF runtime 10.0.11
- 초기 Git: bf00ccb, .gitignore 사용자 수정 및 미추적 AGENTS.md 존재. 두 파일의 기존 내용과 Document 원본 보존
- 기존 사용자 .gitignore의 /docs/ 규칙 유지. 이 검증 기록과 PROJECT_GUIDE는 로컬에서 갱신됐으나 Git ignore 대상
- 명령 도구 기본 샌드박스의 ACL 초기화 오류로, 승인된 동일 저장소 범위 PowerShell 실행을 사용
- 실제 두 번째 PC와 Enterprise 환경을 사용할 수 없었음

## 실행한 검증

1. `dotnet restore IntegratedContro.sln --locked-mode`: 고정 SDK/중앙 버전/잠금 파일로 복원.
2. `dotnet build IntegratedContro.sln --no-restore`: 제품 5개 + 검증 2개 프로젝트, 경고 0 / 오류 0.
3. `dotnet test tests/IntegratedContro.Tests/IntegratedContro.Tests.csproj`: 최종 **38개 테스트**, 건너뜀 없음.
   최종 TRX: `artifacts/test-results/first-implementation.trx`.
4. `dotnet run --project tests/IntegratedContro.UiSmoke -- --profile-dir <격리된 테스트 설정 폴더>`:
   실제 WPF 창/실제 ViewModel과 별도 HTTPS 호스트로 흐름 검증.
   다섯 탭 렌더링을 생성해 확인했으며 WPF 바인딩 경고/오류 없음.
   `artifacts/ui-smoke/result.txt`, `binding.log`, `tab-1.png`~`tab-5.png`.
5. 같은 스모크 도구에서 운영용 `IntegratedContro.App.exe`를 별도 프로세스 2개로 실행해
   각각 정상 주 창을 노출하는 것을 확인. `artifacts/ui-smoke/exe-startup.txt`.
6. `scripts/publish.ps1`: win-x64 self-contained 호스트·앱 폴더 생성.
   `artifacts/publish/ControlHost`, `artifacts/publish/App`.
   새 PC 설치/복사 후 실행은 별도 미검증.

`scripts/verify.ps1`은 잠금 복원 → 빌드 → 테스트 → WPF 스모크를 순서대로 실행한다.
테스트는 `artifacts/process-tests/<임의 ID>` 또는 임시 테스트 하위 폴더만 사용하며 운영 데이터 폴더를 선택/변경하지 않는다.
실행한 테스트 호스트 및 앱 프로세스는 종료한다. 테스트 증거는 artifacts에 남긴다.

## 확인한 경계

| 동작 | 검증한 결과 |
|---|---|
| 최초 관리자·계정 | 교체 가능한 초기 등록 정책, 실제 PBKDF2 검증, 중복 setup 거부, 기존 설정만 남은 DB 유실도 재초기화 거부 |
| 데이터 경로 | 로컬 절대 경로만 허용, 없는 기존 경로는 생성하지 않음, 네트워크 경로 거부, 동일 폴더 중복 호스트 차단 |
| 사용권 경쟁 | 같은 계정 16개 세션 경쟁과 서로 다른 계정 경쟁에서 정확히 한 소유자 |
| 접수/종료 경합 | 30회 동시 실행에서 접수 완료 기록 또는 미접수 거부로 경계 확정 |
| 중복 요청 | 같은 ID 20회 서비스 동시 요청 및 실제 HTTPS 10회 중복 요청이 한 작업으로 수렴, 내용 변경 충돌 거부 |
| 늦은 요청 | 반납 후/같은 세션 재획득 후 이전 세대 요청, timeout 후 늦은 heartbeat, 복구 인계 후 차단된 세션, 재시작 전 토큰 거부 |
| 교대·작업 보존 | 원 요청자/원 세션/예약 유지, 모든 세션 logout 후 호스트에서 명령·시나리오 계속 실행 |
| 선택 취소 | 다음 운영자의 허용 대상 취소, 조회자/범위 밖 취소 거부, 원 요청자 및 취소자 별도 기록 |
| 취소/전송 경합 | 전송 전·전송 중·40회 동시 취소 경합에서 미전송 차단과 전송 결과 보존, 중복 전송 없음 |
| 시나리오 예약 | 이후 단계·역할 별칭까지 같은 장비 충돌, 비예약 장비 접수, STOP 우선 처리 |
| 수동 전환 | 전환 안내 취소 시 시나리오 유지, 명시적 중단 시 후속 단계 차단, 늦은 결과/Continue로 자동 재개 안 됨, 조회·대조 전 수동 차단 |
| snapshot | 외부 반환 객체 변경/역할 재배정/장비 설정/시나리오 변경이 기존 snapshot을 변형하지 않음 |
| 전송 재검증 | 원 요청자 권한 회수, 대상/정의 변경, 만료, 조건 조회 도중 설정 변경을 전송 전에 차단 |
| 불확실 결과 | Continue 우회 없이 중단, 대상 제한, 조회가 과거 명령을 성공으로 덮지 않음 |
| 재시작 | 설정/계정/미전송 일반 작업 복구, 중단 시나리오 자동 재개 없음, 저장된 전송 의도는 불확실 복구 |
| 실제 EXE 강제 종료 | 가상 값 반영 후 응답 유실 상태에서 별도 호스트 프로세스 강제 종료·동일 DB 재시작, 전송 이력 1회 및 불확실 결과 유지 |
| 가상 상태 | PC ID가 다르면 같은 장비 ID/이름도 다른 가상 값, 재시작 후 시뮬레이터 값 보존, 대상 변경 후 옛 관측 초기화 |
| 저장 실패 | 접수 commit 실패 시 성공 응답·메모리 작업 없음, 후속 제어 fail closed |
| 복구 인계 | 차단 확인 전 소유권 발급 없음, 관리자만 작업 확인/승인, 검토 이후 진행 상태 변경 시 재검토 필요 |
| TLS | 실제 Windows Schannel 호스트 연결, WPF 클라이언트의 잘못된 SHA-256 지문 거부 |
| WPF | 실제 창에서 등록·명령·계정·시나리오·교대·취소·수동 전환·가상 상태 대조, 폴링 중 조작 선택 유지, 모든 창 정상 종료 후 작업 완료 |

최초 EXE 검증에서 Schannel의 ephemeral key 오류를 발견해 사용자 키 저장 방식으로 수정했다.
최초 self-contained 잠금 복원에서 RID 불일치를 발견해 공통 RuntimeIdentifiers와 10.0.11 런타임을 고정했다.
이 두 실패는 해결 후 최종 검증에 반영했으며 실패 실행을 성공으로 세지 않았다.

## 검증 범위와 남은 항목

- HTTPS 클라이언트들은 **같은 PC의 loopback** 연결이다.
- WPF 업무 검증은 **같은 테스트 프로세스 내 WPF 창 두 개/실제 ViewModel 실행**이다.
  추가로 **운영 앱 EXE 별도 프로세스 두 개의 시작**을 확인했다.
  운영 앱 두 프로세스 각각의 모든 버튼을 실제 입력 도구로 클릭한 검증은 아니다.
- Windows UI 자동화용 node_repl 런타임이 현재 도구에 노출되지 않아 네이티브 클릭 자동화는 수행하지 않았다.
  코드 기반 WPF 스모크와 RenderTargetBitmap 시각 확인으로 보완했다.
- **실제 서로 다른 두 PC**, LAN/방화벽·실제 손실/지연, Enterprise, 로그인/로그아웃·재부팅,
  100/150/200% 배율 및 장시간 운영은 미검증이다.
- self-contained 폴더를 새 PC로 복사한 실행, 설치·업데이트·인증서 갱신·비밀번호 분실/실행 계정 이관도 미검증/후속이다.
- Hiperwall·영상·실장비·원격 DeviceAgent·Semantic·Windows 서비스·자동 실행은 이번 범위에 포함하지 않았다.
- 전체 상태/작업을 전송하는 소규모 현장 초기 저장/조회 구현이다. 대규모 이력 분할·페이지 조회·보존/백업 정책은 후속이다.

실제 두 PC 수행 순서는 [OPERATIONS.md](../../OPERATIONS.md)의 마지막 절을 사용한다.
10절의 전체 완료 판정은 이 후속 현장 검증을 완료한 뒤 갱신한다.


## 2026-09-09 역할 배정 화면 수정 검증

- 장비 제어에 역할 배정 입력과 대상 표시를 추가하고 관리자 설정에 직접 대상 선택 목록을 제공했다.
  자동 갱신 시 장비 목록 전체 초기화를 없애 선택/목록 열림 상태를 유지한다.
- `dotnet build IntegratedContro.sln --no-restore`: 경고/오류 0.
- 기존 테스트 38개 통과, 실패/건너뜀 0. 결과: `artifacts/test-results/role-assignment-fix.trx`.
- 실제 WPF 창의 DataGrid·TextBox·ComboBox 바인딩과 ButtonAutomationPeer 호출로 배정 검증.
  빈 역할/대상 없음 차단, 자동 갱신 중 선택/입력 유지, 저장한 역할 선택, 같은 이름·서로 다른 PC ID 장비의 재배정,
  열린 대상 목록 유지, 탭 이동 후 선택 유지, 사용 종료/운영자 권한 배정 차단, 명령 snapshot의 정확한 PC·장비 ID 확인을 통과했다.
- 기존 시나리오·교대·작업 유지/취소·창 종료 흐름도 WPF 스모크에서 통과. 바인딩 경고 없음.
  `artifacts/ui-smoke/role-assignment.png`를 시각 확인했다.
- `dotnet publish src/IntegratedContro.App/IntegratedContro.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o artifacts/publish/App-role-fix` 성공.
- 테스트는 격리 호스트/프로필로 수행했다. 사용자 실행 중인 호스트·앱·운영 DB는 변경하지 않았다.
  네이티브 마우스 입력 검증과 실제 두 PC 검증은 수행하지 않았다.


## 2026-09-09 조명 카드·순서 편집 검증

- 빌드 경고/오류 0. 테스트 **43개 통과**, 실패/건너뜀 0: `artifacts/test-results/lighting.trx`.
- 추가 서비스 검증: 표시 순서 공유/SQLite 재시작 복구, 접수 snapshot·설정 버전 유지, 순서 동시 저장 경쟁,
  버전 충돌/중복/누락/알 수 없는 장비 ID 거부, 관리자/사용권 조건.
- 추가 전원 검증: 같은 요청 ID 동시 접수의 단일 작업 보장, 남은 작업 중 새 전환 차단, 정상 사용 종료 후 기존 요청 조회,
  늦은 신규 요청 거부, 오래된 PC ID·장비 ID·설정/역할 버전·상태/시각 거부, 시나리오 예약·불확실 상태·범위 권한,
  전송 전 최신 상태 조건 비교.
- 실제 WPF 창에서 ButtonAutomationPeer로 카드 버튼을 호출해 ON→OFF와 정확한 대상 snapshot을 확인했다.
  조명만 분류, 전원 전용/조광 모델 표시, 미확인 상태 차단, 연속 클릭 차단, 순서 편집/취소/저장,
  별도 HTTPS 세션에서 저장 순서 조회, 재접속 유지, 조회 상태 차단, 예약/수동 전환/대조 흐름을 통과했다.
- 기존 WPF 흐름도 통과했다. 처음 조명 검증의 종료 단계에서 창 Closing 재진입을 발견해 수정한 뒤
  로그아웃 후 종료를 포함해 전체 WPF 스모크를 다시 실행하고 프로세스 종료 코드 0을 확인했다.
- 시각 확인: `artifacts/ui-smoke/lighting-cards.png`, `lighting-compact.png` (1180×860 설정의 최소 창).
  `lighting-order.png`는 순서 편집 화면이다. `lighting-binding.log`는 바인딩 경고 없이 비어 있다.
  결과: `lighting-result.txt`. 새 호스트/App의 win-x64 self-contained Lighting 폴더 publish 성공.
- 물리 터치 패널/실제 마우스 입력 자동화/다른 PC 및 DPI별 실기기는 미검증이다.
  테스트의 가상 장비와 호스트는 격리된 테스트 데이터이며 사용자 호스트·등록 장비·계정·작업은 수정하지 않았다.


## 2026-09-09 조명 그룹·축소 카드·드래그 검증

- dotnet build IntegratedContro.sln -c Release --no-restore: 경고/오류 0.
- 테스트 **46개 통과**, 실패/건너뜀 0: artifacts/test-results/lighting-groups.trx.
  추가 검증은 그룹의 다른 세션 공유·SQLite 재시작 유지, 기존 순서 전용 요청의 그룹 보존,
  중복 ID/이름/구성원·알 수 없는 대상 거부, 그룹 삭제 시 장비·설정 버전·snapshot 유지,
  Groups 필드가 없는 기존 JSON의 순서/버전 유지다.
- dotnet run --project tests/IntegratedContro.UiSmoke -c Release --no-build --no-restore: 종료 코드 0.
  기존 역할 배정·카드 전원·시나리오·교대·취소·종료 검증과 그룹 추가/이름 변경/삭제/저장/취소를 통과했다.
  저장한 그룹을 별도 loopback HTTPS 세션에서 확인했다.
- 실제 WPF HitTest 결과에 합성 마우스 PreviewMouseDown을 전달해 편집 입력 처리를 확인했다.
  실제 OS 버튼을 누른 상태가 아니므로 마우스 캡처 유지 여부를 하드웨어 검증으로 사용하지 않았다.
  초기 검증의 캡처 유지 가정은 수정했고, 제품 코드에는 캡처 실패 즉시 취소 처리를 추가한 뒤 다시 통과했다.
  합성 WPF TouchDevice의 터치 시작/종료에서 이벤트 처리와 캡처/해제를 확인했다.
- 실제 WPF 시각 트리의 위치를 사용해 제품의 공통 드래그 경로를 마우스/터치 포인터 ID로 실행했다.
  작은 움직임, 카드 앞 삽입, 빈 그룹으로 이동, 다른 손가락 무시, 영역 밖 놓기, 취소,
  자동 갱신 중 초안 유지, 사용권 상실 뒤 늦은 드롭 차단, 일반 모드의 드래그 차단을 통과했다.
  그룹 편집/드래그로 전원 작업이 추가되지 않음을 확인했다.
- 카드 두 개의 같은 행 배치와 정상/편집/최소 창 화면을 렌더링했다.
  artifacts/ui-smoke/lighting-groups.png, lighting-groups-edit.png, lighting-compact.png,
  lighting-group-drag-result.txt에 증거가 있다. lighting-binding.log는 바인딩 경고 없이 비어 있다.
- 잠금 복원으로 win-x64 self-contained Current/ControlHost, Current/App publish 성공.
  기존 Lighting/App-role-fix 배포본, 실행 중인 사용자 앱·호스트, 운영 데이터는 변경하지 않았다.
- **물리 마우스/터치 패널 드래그, 실제 두 PC, DPI별 하드웨어, 네이티브 입력으로 가장자리 스크롤/캡처 상실은 미검증**이다.
  현장에서는 같은 그룹 앞/뒤, 다른 그룹, 빈 그룹, 화면 밖 놓기, 길게 끌며 스크롤, 터치 후 전원 오작동 없음,
  저장/취소 및 다른 PC 재접속을 확인한다. 로컬 합성 입력 결과를 실기기 검증으로 표시하지 않는다.


## 2026-09-09 접속 팝업·관리자 탭·감사 기록·일괄 조명 검증

- dotnet --version: 10.0.400. SDK·중앙 패키지·잠금 파일 버전은 변경하지 않았다.
- dotnet build IntegratedContro.sln -c Release --no-restore: 경고/오류 0.
- dotnet test tests/IntegratedContro.Tests -c Release --no-build --no-restore
  --logger 'trx;LogFileName=login-bulk-audit.trx' --results-directory artifacts/test-results:
  **59개 통과**, 실패/건너뜀 0.
- 새 일괄 검증: 중복 동시 요청 12개의 단일 작업, 정확한 그룹 대상 snapshot, 접수 후 그룹 변경 불변,
  사용 종료 뒤 작업 유지·동일 ID 조회 및 늦은 신규 요청 거부, 대상/상태/목록 변경·예약·불확실 상태의 전체 접수 거부,
  범위 권한·각 전송 전 권한 재검증, 다음 운영자의 미전송 부분 취소와 전송 결과/원 요청자 보존.
- 절대 ON이 토글로 바뀌지 않음, 내용 변경/다른 API의 요청 ID 충돌,
  최신 상태 조건 차단, 실패·불확실 이후 후속 단계 차단,
  전송 전 작업 재시작 복구와 이미 시작한 일괄 작업 자동 재개 금지를 통과했다.
  구형 enum 계약이 LightBatch를 일반 명령으로 읽지 못함도 검증했다.
- 감사 검증: 새 이름/설명을 발생 시점에 저장하고 이후 이름 변경·재시작에도 유지,
  원본 ID·시각·내용 보존, 기존 기록의 표시용 해석, 가상 완료/전송 준비 구분,
  감사 내용에 비밀번호가 포함되지 않음을 확인했다.
- dotnet run --project tests/IntegratedContro.UiSmoke -c Release --no-build --no-restore
  -- --profile-dir artifacts/ui-smoke/profile-login-bulk: 종료 코드 0.
  시작 모달 팝업의 실제 TextBox/PasswordBox 바인딩, 잘못된 비밀번호의 팝업 유지·비우기,
  성공 후 닫힘, 상단 로그아웃 후 다시 열림, 관리자/운영자 탭 표시와 로그아웃 시 선택 탭 해제를 확인했다.
  비밀번호의 프로필 미저장도 확인했다.
- 실제 WPF 전체/그룹 ON/OFF ButtonAutomationPeer 호출로 별도 HTTPS 호스트의 고정 대상·절대 결과·그룹 밖 유지,
  편집/사용권 없음 차단, 미확인 장비가 있을 때 부분 접수 없음, 한국어 감사 설명과 원본 상세를 확인했다.
  기존 역할 배정·드래그·시나리오·교대·취소·창 종료 흐름도 통과했다.
- 감사 표의 초기 열 폭/줄 높이를 보완한 뒤 최종 화면을 다시 확인했다.
  증거: artifacts/ui-smoke/login-popup.png, operator-home.png, readable-audit.png, lighting-bulk.png,
  lighting-compact.png, login-result.txt, lighting-bulk-result.txt.
  WPF 바인딩 경고 없음. 팝업 캡처 시 비밀번호 입력칸은 비워져 있다.
- UiSmoke는 저장소 artifacts 하위의 --profile-dir 없이는 시작하지 않아 실제 사용자 접속 설정을 보호한다.
  이번 실행은 격리 프로필과 테스트 DB/호스트만 사용했고 사용자 실행 중인 호스트·운영 DB는 변경하지 않았다.
- 잠금 복원으로 Current/App, Current/ControlHost의 win-x64 self-contained 배포본을 생성했다.
- 물리 터치 패널, 실제 두 PC·LAN/방화벽, Enterprise·DPI별 하드웨어는 미검증이다.
  현장에서는 두 PC 로그인/사용권, 전체·그룹 대상 결과, 접수 후 로그아웃/교대/선택 취소,
  통신 단절 시 동일 ID 재확인과 부분 전송 후 재시작을 확인한다. 실장비·서비스·자동 실행은 이번 범위가 아니다.

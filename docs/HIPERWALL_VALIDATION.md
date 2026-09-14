# Hiperwall 연결·편집 구현 검증 기록

최근 검증일: 2026-09-14. 구현 완료·가짜 서버 검증·실제 Controller 검증을 구분한다.

## 2026-09-14 F2·F3·F4 수정 검증 (소스 반영)

- 전체 빌드 경고 0/오류 0, 서비스·통합 테스트 **149개 통과** (기존 127 + 대상/표시 이름 18 + Hiperwall 교대 4).
- /api/state의 Hiperwall 미완료 목록·원 세션 보존, 다음 사용권자의 선택 취소,
  조회/범위 제한 계정의 취소 거부, 완료 기록 제외·불확실 기록 유지,
  최근 100건보다 오래된 Unknown 노출과 재시작 보존을 검증했다.
- 실제 WPF **작업 · 교대** 화면을 별도 HTTPS 호스트와 가짜 Controller로 확인했다.
  Hiperwall 탭을 열거나 이력 새로 고침을 하지 않아도 로그인 후 이전 작업 1건이 표시된다.
  조회 시 취소 차단, 사용 시작 후 미전송 단계만 취소, 전송 중인 명령의 늦은 Unknown 자동 갱신,
  로그아웃 시 목록 제거·재로그인 자동 표시와 원 요청자·세션·대상 표시를 검증했다.
- 근거: artifacts/test-results/f2-f4-regression.trx, artifacts/ui-smoke/handover-result.txt,
  artifacts/ui-smoke/handover-hiperwall.png, handover-hiperwall-small.png.
- 기존 로그인·일반 운영·조명·Hiperwall·포인터·Zone의 WPF 전체 회귀도 통과했다. 새 교대 화면은 1180×860에서도 목록 스크롤·취소 버튼·상세 표시를 확인했다.
- 운영 Current 교체·운영 호스트 재시작은 하지 않았다. 실제 Controller·두 PC·Enterprise는 미검증이다.

## 2026-09-14 Zone 바로가기 검증 (11:21 배포)

- 캔버스 아래 Zone별 버튼, 선택/드롭 색상, 같은 이름의 ID 구분, 버튼 클릭 시 Zone 화면 이동을 구현했다.
  버튼 클릭은 제어 명령을 보내지 않고 소스 선택을 유지한다. 사용권이 없는 조회 모드에서도 탐색한다.
- 캔버스 소스를 마우스/합성 WPF 터치로 버튼에 드롭하여 대상 Zone 중앙으로 정확히 한 번 이동하는 것을 확인했다.
  이동 전 크기 2560.25×1440.5처럼 Zone보다 큰 소수 크기를 그대로 유지하고 입력칸의 미적용 초안은 사용하지 않는다.
- Contents의 실제 WPF DragOver/Drop 경로로 버튼에 추가한 소스도 원본 2560×1440을 유지했다.
  취소/오래된 소스 항목/사용권 없는 드롭은 명령을 보내지 않는다.
- 최종 Release 빌드 경고 0/오류 0, 서비스·통합 테스트 **121개 통과**, Hiperwall 조회·편집·기존 포인터·Zone 버튼 WPF 검증 통과.
  근거: `artifacts/test-results/hiperwall-zone-shortcuts.trx`, `artifacts/ui-smoke/hiperwall-zone-shortcut-result.txt`.
  최소 1180×860에서 캔버스·Zone 버튼·하단 제어 표시를 확인했다. 최신 `hiperwall-editor*.png`는 가짜 데이터다.
- `Build-20260914-112136` 배포 완료. 실행 중인 Current 호스트의 349개 파일이 새 배포와 모두 동일함을 확인하고,
  실행 중이지 않은 App만 교체했다. 이후 새 배포의 751개 파일이 Current와 일치하는 것을 검증했다.
  `publish-result.json`의 currentApplied=true / applicationOnlyUpdate=true / controlHostVerifiedUnchanged=true.
- 운영 호스트를 중지하거나 운영 데이터를 열지 않았다. 물리 마우스·터치패널·실제 Controller는 미검증이다.

## 2026-09-14 드래그·터치 후속 검증 (11:03 배포)

- 재현: Object zone이 없는 인스턴스를 선택하면 사용자가 미리 선택한 Zone을 지워 CanMove가 false가 됐다.
  WPF 테스트가 `Selecting a zone-less instance discarded the explicitly chosen Zone`으로 실패하는 것을 확인한 뒤 수정했다.
- 기존 드래그 검증은 내부 이동 함수만 호출했다. 이제 마우스 선택/편집 입력 경로와
  실제 뷰/바인딩을 통한 합성 WPF TouchDevice Down/Move/Up·캡처 이벤트를 검사한다.
- 선택 직후 이동, 명시 Zone 유지, 확대 좌표, 44-DIP 터치 손잡이, 비율 유지/좌상단 고정,
  최종 놓기 좌표, 드래그당 명령 1회, 탭/Zone 없음/캡처 상실/선택 변경/두 번째 터치/사용권 상실 시 미전송을 확인했다.
- Release 전체 빌드 경고 0/오류 0, 전체 서비스·통합 테스트 **121개 통과**, Hiperwall 조회·편집 WPF 검증 통과.
  근거: `artifacts/test-results/hiperwall-pointer.trx`, `artifacts/ui-smoke/hiperwall-pointer-result.txt`.
  `hiperwall-editor.png`, `hiperwall-editor-small.png`은 최신 가짜 데이터 화면이다. 최소 창의 손잡이·안내 배치를 확인했다.
- Windows 입력 도구는 초기화 시 sandbox ACL 오류로 연결되지 않았다. 물리 마우스 입력·실물 터치패널 및 실제 Controller는 미검증이다.
  한 손가락 이동/손잡이 크기 조절 테스트이며 두 손가락 핀치 테스트가 아니다.
- 점검 당시 Current/App은 9월 10일 조회 전용 앱이었다. 새 `Build-20260914-110315`의
  App과 ControlHost를 **Current에 모두 반영했다(currentApplied=true)**. 배포 파일 해시가 모두 일치한다.
  운영 데이터는 열거나 변경하지 않았으며 운영 서버를 시작하거나 강제 종료하지 않았다.

## 2026-09-14 LIVE 편집 검증

- 전체 서비스·통합 테스트 **121개 통과** (기존 107 + Hiperwall 편집 14).
- 로컬 가짜 Controller와 별도 HTTPS ControlHost를 사용한 실제 WPF 창 검증 통과.
  소스 원본 크기 추가/자동 선택, 수치 좌표·크기, 드래그 이동/비율 유지 크기 조절,
  음량·음소거/전체 해제, Zone 이동 크기 유지, 조회/사용권 차단, 전체 닫기 확인/취소,
  전송 이력을 검증했다. 전체 로그인·조명·교대 WPF 회귀도 통과했다.
- 전송 직전 대상 변경, 권한 회수, 중복 요청 재사용, 원문 이름/XML 특수문자,
  전체 닫기 revision/부분 실패, 응답 유실/닫기 404 재조회, 미지원 audio/Shadow,
  정상 로그아웃 뒤 접수 작업 실행과 재시작 재전송 차단을 검증했다.
- 최소 1180×860 화면의 전역 제어 잘림과 필터 선택 유지를 회귀 검증에 포함했다.
- 화면 증거: artifacts/ui-smoke/hiperwall-editor.png, hiperwall-editor-small.png (가짜 데이터).
- 새 NuGet 의존성 없음. 공개 API와 명령에 토큰·XML 원문을 노출하지 않는다.
- 실제 Controller·물리 마우스/터치·두 PC·Enterprise는 미검증이다. 실제 장비에 시험 명령을 보내지 않았다.
- 초기 편집 배포(후속 배포로 대체됨): artifacts/publish/Build-20260914-103857/App 및 ControlHost. 당시 Current 적용 false: 기존 운영 호스트 실행 중.
  실행 안내는 같은 폴더의 실행 안내.md, 기계 판독 결과는 artifacts/publish/publish-result.json이다.
  운영 서버를 강제 종료하거나 실제 Controller에 시험 명령을 보내지 않았다.

## 2026-09-10 조회 작업 공간 확장 결과

최종 빌드·검증·배포를 완료했다. 실제 표시 변경 범위에 대한 추가 확인 전이므로 읽기 전용을 유지한다.
이 절과 아래 2026-09-09 기록은 과거 조회 단계의 증거다. 최신 결과는 2026-09-14 절을 따른다.

| 확인 | 실행 결과 |
|---|---|
| 전체 빌드 | dotnet build IntegratedContro.sln --no-restore: 경고 0, 오류 0 |
| 전체 서비스·통합 테스트 | **107개 통과**. 기존 91개와 새 인스턴스·좌표 검증 16개. artifacts/test-results/hiperwall-workspace.trx |
| 추가 읽기 프로토콜 | 가짜 서버에서 GET /hello, list, walls, list/filter=open만 허용. 실제 콘텐츠 open/change/close 요청 없음 |
| 인스턴스 | 같은 한글 이름과 같은 콘텐츠 UUID의 서로 다른 Instance ID, 원문 필드, 빈 응답, 필터 무시·미지원 API, ID 누락·중복, NaN·잘못된 크기, XML DTD 차단 검증 |
| 좌표 | 음수·소수 Zone 원점, Instance 중심/Y 부호 변환, 좌표 미제공·회전·표시 범위 초과의 캔버스 제외를 검증 |
| WPF | 실제 창·생산 ViewModel·별도 HTTPS 테스트 호스트로 검색(UUID/검색 없음/유형/경로 분류), 목록·캔버스 선택, 확대·선택 맞춤·전체 맞춤, 최소 1180×860 레이아웃 확인 |
| WPF 수명 | 미지원 인스턴스 도형 제거, 설정 변경 중 지연 응답 폐기, 중복 새로 고침, 로그아웃·창 종료 시 인스턴스·캔버스 비우기. 바인딩 오류 없음 |
| 기존 흐름 | 접속·일반 운영·가상 조명 WPF 검증 통과. 서비스 회귀에서 사용권·작업 보존·교대 등 기존 테스트 통과 |
| SDK·패키지 | global.json, Directory.Build.props, Directory.Packages.props, packages.lock.json 변경 없음. 새 NuGet 없음 |
| self-contained | Release win-x64 App/ControlHost 생성. Current 반영 true |
| 배포 EXE | Current 앱에 격리 프로필을 주어 실제 창 생성 확인 후 해당 테스트 프로세스 정리. Current 호스트는 인자 없는 사용법/종료 코드 2 확인. 운영 DB를 열지 않음 |
| 실제 Controller | 이번 확장에서는 요청을 보내지 않음. 버전·인증·실제 목록·좌표 호환성은 미검증 |

WPF 실행에서 찾은 Run.Text 읽기 전용 바인딩 오류, 그룹 목록의 폭, 작은 창의 캔버스·목록 높이를
수정하고 최종 Hiperwall 실행 검증을 다시 통과했다. 코드로 구동한 실제 창 검증이며 물리 마우스·터치 입력 검수는 아니다.
실제 현장 프리뷰, 회전 방향, DPI/다중 모니터, 버전별 open 필터·ID 수명은 현장 검증이 필요하다.

최신 증거:
- artifacts/ui-smoke/hiperwall-result.txt
- artifacts/ui-smoke/hiperwall-workspace.png, hiperwall-workspace-small.png — **가짜 Controller 데이터** 화면
- artifacts/published-workspace-smoke-result.txt
- artifacts/publish/publish-result.json
- 보존 배포본: **artifacts/publish/Build-20260910-094016/App**, **ControlHost**
- 적용 실행 파일: **artifacts/publish/Current/App/IntegratedContro.App.exe**
- 적용 호스트: **artifacts/publish/Current/ControlHost/IntegratedContro.ControlHost.exe**

앱과 호스트를 함께 업데이트하고 기존 데이터 폴더·Windows 실행 계정을 유지한다.
사용자 프로세스·참고 저장소·운영 데이터·기존 사용자 변경을 보존했다.

## 2026-09-09 최초 연결·조회 기록



## 구현 범위

단일 활성 연결 설정, None/Token 인증, DPAPI 보호 저장, 읽기 전용 연결 테스트,
Contents 기본 목록과 Zones 응답 조회, Wall 응답 유형/미지원 표시, 항목 상세·수동 새로 고침,
권한·사용권·설정 버전 검사, 조회 취소·응답 순서 보호, 한국어 감사 기록을 구현했다.

Wall의 비어 있지 않은 상세 목록, Crypto, 콘텐츠 표시 변경, Enhanced List,
카메라·영상·Semantic 전체, 실장비 드라이버는 범위 밖이다.
상세 근거는 [HIPERWALL_PROTOCOL.md](HIPERWALL_PROTOCOL.md)를 따른다.

## 실행 환경

- Windows 11 Pro, OS 10.0.26200 x64. Enterprise·다른 PC 조합은 이번에 검증하지 않았다.
- SDK 10.0.400 / .NET 10 / 런타임 10.0.11 / C# 14.
- 기존 중앙 패키지 버전과 packages.lock.json을 변경하지 않았다. 추가 NuGet 의존성 없음.
- 처음 Git 작업 트리는 깨끗했다. 사용자 변경·Document 원본·운영 데이터는 수정하지 않았다.
- 파일 접근 도구의 Windows 샌드박스 초기화 오류 때문에 승인된 저장소 범위의 확장 셸로 읽기·편집·빌드를 수행했다.
- 테스트는 artifacts와 테스트 전용 임시 폴더를 사용한다. 종료한 프로세스는 테스트에서 생성한 프로세스뿐이다.

## 실행한 검증

| 검증 | 실제 결과 |
|---|---|
| dotnet restore IntegratedContro.sln --locked-mode | 통과 |
| dotnet build IntegratedContro.sln --no-restore | 경고 0 / 오류 0 |
| 전체 서비스·통합 테스트 | 91개 통과: 기존 59개 + Hiperwall 32개 |
| Hiperwall 기본 요청 | 가짜 TCP 서버에서 GET /hello, POST /xmlcommand의 list/walls만 수신한 것을 확인 |
| 정상·잘못된 인증 | Token XML 이스케이프와 사용자·토큰 일치, None, 인증 불일치, 401/403, HTTP 200 내 Error를 확인 |
| 오류와 제한 | 시간 초과, 연결 끊김, 404/405/408/500/302, 빈 목록, 잘못된 XML·DTD/외부 엔터티·과대 응답·깊은 XML·NaN 좌표 확인 |
| 목록 구분·식별 | 한글·동일 이름의 서로 다른 UUID/Zone ID, UUID 없음, ID 중복 차단, Zones/Walls/미지원 상세 Wall 구분 |
| 수명 | 동일 세션 중복 조회 거부, 서로 다른 세션의 역순 완료, 설정 변경 후 늦은 결과 폐기, 명시적 취소, 로그아웃·계정 비활성 후 결과 차단 |
| 저장·보안 | 동일 지정 DB 재시작 복구, 토큰 재사용/새 대상 재입력, API·DB·암호문 파일의 원문 비노출, DPAPI 파일 손상 시 명시적 실패, 누락 폴더 대체 생성 금지 |
| 감사 | 설정 변경·명시적 테스트의 한국어 이벤트, 일반 새로 고침에 감사 이벤트가 쌓이지 않음 |
| 회귀 | 기존 가상 조명·그룹·일괄 제어·사용권·snapshot·작업 보존/취소·교대·복구 테스트 통과 |
| WPF | 실제 STA 창·생산 ViewModel·별도 HTTPS 호스트·가짜 Controller를 이용한 실행 검증 통과, 바인딩 오류 없음 |
| WPF 조회 흐름 | 설정 없음, 저장·적용·토큰 입력칸 비우기, 목록/상세 선택, 중복 이름 UUID 유지, 빈/미지원, 중복 새로 고침, 원격 설정 변경·지연 응답, 로그아웃·창 종료 확인 |
| WPF 기존 흐름 | 접속 팝업·관리자 탭 숨김·조명·역할·그룹·일괄 제어·한국어 감사·교대·작업 흐름 재확인 |
| self-contained 배포 | Release win-x64 App/ControlHost 모두 생성, Current 반영 완료 |
| Current EXE 실행 | 앱의 독립 프로필 창 생성 확인 후 테스트 프로세스 정리. 호스트는 인자 없이 실행해 명시적 데이터 폴더 사용법/종료 코드 2 확인 |

WPF 검증은 코드로 구동한 실제 창 검증이다. 사람이 물리 입력한 현장 검수·실제 두 PC 테스트와 구분한다.
실제 TLS 인증서 핀/로그인·Hiperwall 조회는 별도 테스트 호스트 EXE로 검증했고,
Current 호스트 자체의 현장 데이터 운영은 시작하지 않았다.

## 검증 파일

- artifacts/test-results/hiperwall-final.trx — 최종 91개 결과.
- artifacts/ui-smoke/hiperwall-result.txt — WPF Hiperwall 시나리오 결과.
- artifacts/ui-smoke/hiperwall-unconfigured.png, hiperwall-settings.png,
  hiperwall-contents.png, hiperwall-zones.png, hiperwall-unsupported.png — 가짜 서버 화면.
- artifacts/ui-smoke/ — 기존 조명·접속·감사 증거.
- artifacts/published-smoke-result.txt — Current 앱 EXE 실행 확인.
- artifacts/publish/publish-result.json — 생성 시각·보존 배포 경로·Current 반영 결과.

테스트 실패 후 수정한 사항도 재검증했다. 첫 빌드의 WPF 컨트롤/계약 이름 충돌 및 ASP.NET 비동기 반환 처리,
플랫폼 분석 경고를 수정했다. 연속 성공 조회의 마지막 시각 검사는 최초 성공 대신 가장 최근 성공을 비교하도록 수정했다.
최종 집계는 이전 테스트 바이너리 결과가 아닌 최종 빌드의 결과다.

## 배포본과 적용

생성한 보존본: artifacts/publish/Build-20260909-165547/App 및 ControlHost.
현재 실행 경로:

- C:\Users\노주형\Desktop\IntegratedContro\IntegratedContro\artifacts\publish\Current\App\IntegratedContro.App.exe
- C:\Users\노주형\Desktop\IntegratedContro\IntegratedContro\artifacts\publish\Current\ControlHost\IntegratedContro.ControlHost.exe

앱·호스트 폴더 전체를 함께 배포한다. EXE 한 파일만 옮기지 않는다.
기존 작업을 확인하고 호스트를 정상 종료한 뒤, 기존 Windows 실행 계정과 **동일한 데이터 폴더**로 run한다.
setup을 반복하거나 운영 DB를 새 폴더로 대체하지 않는다.
새 설정을 저장한 데이터는 구형 호스트가 알 수 없는 Hiperwall 필드를 유실시킬 수 있으므로 구형 EXE로 열지 않는다.

publish.ps1은 두 제품을 별도 Build 폴더에 먼저 생성한 뒤 Current의 실행 중 프로세스·파일 잠금을 확인한다.
잠금이나 복사 실패가 있으면 프로세스를 종료하지 않고 Build 폴더와 결과 JSON을 남긴다.
그 경우 정상 종료 후 Build의 App·ControlHost를 모두 교체하거나 그 폴더에서 동일 데이터 경로로 실행한다.
이번 실행에서는 Current 반영에 성공했다.

## 실제 Controller에서 확인한 범위

사용자가 제공한 http://127.0.0.1:8000/hello에 읽기 전용 요청을 한 번 시도했으나 HttpRequestException으로 실패했다.
원문 응답·제품·버전·인증 성공·Wall/Zone/Contents는 확인하지 못했다.
실제 토큰은 받거나 저장하지 않았고 실제 Controller에 인증 요청을 전송하지 않았다.
FixtureSecret은 코드에 명시한 가짜 서버 검증값이며 현장 자격 증명이 아니다.

## 현장에서 필요한 정보·검증

- 정확한 HiperController/HiperInterface 버전, 실제 호스트 기준 주소·포트, HiperInterface 가동·조회 권한.
- 해당 버전의 공식 API 매뉴얼. 특히 비어 있지 않은 Wall 목록의 필드·안정적인 식별자,
  UUID가 없는 콘텐츠의 의미, Walls/Zone 응답 모드.
- 실제 Token은 앱에 입력한 뒤 연결 테스트와 목록·오류·재시작 복구를 확인.
- HTTPS가 필요하면 실제 TLS 구성을 확인. Crypto는 후속 구현 필요.
- 실제 두 PC, Windows Enterprise, 네트워크 장애·현장 계정 권한 차이 검증.
- 토큰 교체로 남은 보호 파일 정리·Windows 실행 계정 변경/이관 도구는 후속 운영 범위.

가짜 서버 성공을 실제 Hiperwall 연결 성공으로 보고하지 않는다. 영상벽 표시 상태를 바꾸는 요청은 구현·실행하지 않았다.

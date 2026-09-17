# Hiperwall 조회·편집 프로토콜 근거

초기 검토일: 2026-09-09. 조회·편집 확장 기록을 포함한다. 현재 저장 배치·슬롯·프리뷰 범위는 [프로젝트 기준](PROJECT_GUIDE.md)을 따른다.
이 문서는 상세 프로토콜의 공식 인증이나 실제 Controller 호환성 인증을 뜻하지 않는다.

## 2026-09-14 편집 명령 확장 (최신)

사용자 요청에 따라 아래 읽기 전용 단계에 쓰기 명령을 추가했다. 참고 근거는 사용자가 지정한
C:/Users/노주형/Desktop/verkada/backend/src/hiperwall.js의 buildOpenXml/buildChangeXml/buildCloseXml/
buildSystemMuteXml과 backend/test/wall-editor.integration.test.js다. 실제 Controller에서 검증된 명세로 간주하지 않는다.

| 동작 | 전송 형식 |
|---|---|
| 추가 | Commands/command type=open, uuid 또는 name, 고유 id, zone, x/y/boundsw/boundsh/boundsfill=1 |
| 위치·크기 | command type=change, id, zone, x/y/boundsw/boundsh/boundsfill=1, queue=0, time=0 |
| 개별 소리 | command type=change, id, volume(0~100 정수) 또는 mute(true/false), queue=0, time=0 |
| 개별 닫기 | command type=close, id |
| 전체 소리 | action type=mute-all 또는 unmute-all |
| 전체 닫기 | 확인된 인스턴스 목록의 각 id에 close. 원격 blanket close를 사용하지 않음 |

좌표는 앱의 중심 X/Y이며 XML 생성 시에만 Y 부호를 반전한다. 음수·소수·0을 보존한다.
원문 전체 이름과 XML 특수문자는 XElement로 인코딩한다. UUID 없는 원문 이름은 전체 목록에서 유일할 때만 쓴다.
Contents의 width/height를 실제 응답에 있을 때 보존한다. 원본 크기 미제공은 사용자 입력으로 처리한다.
audio는 volume,muted 또는 volume,unmuted 형식일 때만 개별 소리 조절을 제공한다.

접수와 전송 전에 최신 연결·목록·대상·설정 버전·앱 권한을 검사한다. Shadow에는 쓰기를 보내지 않는다.
Default/Primary라는 역할은 쓰기 권한 보장이 아니며 Controller의 실제 거부를 별도로 처리한다.
HTTP 2xx 안의 Error도 거부다. Error가 없는 Response는 응답 확인으로 기록하고 실제 상태는 재조회한다.
유실·제한시간·알 수 없는 응답은 결과 확인 필요이며 자동 재전송하지 않는다.
close의 404/유실 후에는 새 목록에서 해당 ID가 없어야 종료 상태를 확인한 것으로 기록한다.
요청 ID·원 요청자·target snapshot·전송 상태는 SQLite에 보존한다. 토큰·원문 XML은 기록하지 않는다.

상세 목록은 Contents fields=label,width,height,said,zone 및 열린 인스턴스의 position/size/audio 등을
참고 코드와 같은 fields 문자열로 요청한다. 형식/API 미지원 응답이면 기본 list로 한 번 전환한다.
버전 숫자로 지원을 추정하지 않으며 기본 응답에 없는 위치·소리를 만들어내지 않는다.
저장 배치/Environment 적용, preview, rotation, Semantic과 Crypto는 이번 범위에 없다.
아래 2026-09-09/10 조회 전용 설명은 당시 구현 기록이며 현재 편집 범위를 제한하지 않는다.

## 근거의 구분

| 항목 | 확인한 내용 | 근거 및 한계 |
|---|---|---|
| 공식 자료 | HiperInterface는 외부 앱용 API이며 HTTP/XML 기반이다. HiperAccess는 인증·권한 기능이다. | [HiperController Add-On Applications](https://hiperwall.com/technology-stack/hipercontroller-add-on-applications/), [Hiperwall 9.0 발표](https://hiperwall.com/hiperwall-9-0-unleashes-new-era-of-video-wall-design-flexibility-content-impact-and-system-recovery/) |
| 참고 코드·문서 | GET /hello, POST /xmlcommand, Commands/auth/action 구조, None/Token 설정 | ../../archive/legacy-reference/backend/src/hiperwall.js, hiperwall-hello.js, ../../archive/legacy-reference/README.md, WINDOWS_APP_REQUIREMENTS.md |
| 참고 테스트 | Objects/Object 및 Zones/Zone 필드, 빈 Walls, 오류 응답 예제 | ../../archive/legacy-reference/backend/test/hiperwall-inventory.integration.test.js, hiperwall-business-error*.test.js, hiperwall-enhanced-list.test.js. 모두 가짜 응답이다. |
| 이번 실제 접속 시도 | 사용자가 제공한 http://127.0.0.1:8000/hello 를 제한시간 3초·리다이렉트 금지로 1회 요청했으나 HttpRequestException 발생 | 응답 제품·버전·인증·목록을 확인하지 못함. 토큰을 전송하지 않았음. |
| 현장 입력 정보 | 사용자가 URL 위 값, 인증 token, 사용자 3, 제한시간 3000ms를 제공 | 현장 입력값이며 실제 연결 성공·버전 확인 근거가 아님. 프로그램 기본 주소·사용자로 고정하지 않음. |
| 미확인 | 상세 공식 API 명세, 실제 버전, 해당 포트의 HiperInterface 활성화, 조회 권한, HTTPS 제공 방식, 실제 XML 구조 | 현장 버전의 HiperInterface 매뉴얼과 실제 읽기 응답을 확보하여 대조해야 함. |

공개 공식 페이지에서는 상세 경로·XML 스키마를 확인하지 못했다. 따라서 이번 어댑터는 **제공된 코드와 문서의 읽기·편집 요청을 지원하는 참고 코드 기반 호환 구현**이다.
기존 구현의 버전 숫자만으로 Enhanced List/Semantic 지원을 추론하는 방식은 채택하지 않았다.
라이선스 보유·HiperInterface 가동 여부도 실제로 확인하지 않았다.

## 전송 범위

1. GET /hello: 참고 문서의 제품, 버전, 인증, 역할 4개 쉼표 구분 필드를 제한적으로 해석한다.
   제품은 Hiperwall, 역할은 Default/Primary/Shadow, 인증은 None/Token을 지원한다.
   Crypto는 미지원으로 종료한다. 버전은 응답값만 표시하며 버전별 기능 플래그를 생성하지 않는다.
2. POST /xmlcommand: Commands 아래 Token 인증일 때 auth type="token"의 user/token을 넣고,
   action type="list" 또는 action type="walls" 하나만 전송한다.
   이는 참고 구현에서 목록을 반환하는 요청이다. POST라는 메서드 이름으로 쓰기로 분류하거나,
   GET이라는 이유로 임의 경로를 읽기라고 인정하지 않는다.
3. 기본 Contents list는 필터·fields 없는 요청이다. 2026-09-10부터 별도 list/filter=open 읽기를 추가했다(아래 확장 절 참조).
   enhanced fields/preview/search-semantic 및 open/change/close, mute, 배치 표시, 저장된 Environment 실행은 구현하지 않았다.
4. URI 경로와 XML 동작은 어댑터 내부 허용 목록에 고정한다. API에 임의 경로·명령·XML 전달 기능이 없다.
   인증이 설정과 다르면 XML 목록 요청 전에 중단한다. /hello 성공만으로 연결 테스트를 성공 처리하지 않는다.
5. HTTP/HTTPS와 주소·포트는 현장 입력이다. 포트는 명시적으로 입력한다.
   HTTPS는 .NET의 정상 인증서 검증을 사용하며 검증 우회는 없다. 현장 TLS 구성이 미확인이다.
   HTTP에서는 호스트→Controller 구간의 Token이 암호화되지 않는다. 앱→호스트 구간은 기존 HTTPS·지문 검증을 유지한다.
6. 전체 조회는 100~30000ms의 한 제한시간을 공유한다. /hello 4KiB, XML 4MiB, 목록당 10000개,
   필드 길이 4096자, XML 깊이 16을 제한한다. DTD 금지·XmlResolver=null·외부 자원 접근 차단,
   UTF-8 검사, HTTP 리다이렉트·쿠키·프록시 자동 사용 금지를 적용한다.

## 목록 출처와 식별자

| 화면 | 데이터 출처 | 구현한 해석과 한계 |
|---|---|---|
| Contents | 이번 list 응답의 Objects/Object | name, uuid, type, label. 이름 중복을 유지하며 uuid가 없으면 식별자 없음으로 표시한다. unknown 필드를 실제 필드처럼 생성하지 않는다. |
| Zone | 이번 walls 요청이 Zones/Zone을 반환한 경우 | id, name, color, left/top/width/height/zonegridh/zonegridv가 실제로 있을 때만 표시한다. 소수·음수 좌표 원문을 유지한다. |
| Wall | 이번 walls 요청의 응답 유형 | 빈 Walls는 조회 성공·빈 목록으로 표시한다. Wall 자식이 있는 응답은 상세 필드와 안정적인 ID 명세가 미확인이므로 목록 해석 미지원으로 표시한다. |
| Zone 미지원 | Walls 응답 | Zone 목록이 제공되지 않았다고 표시한다. 참고 코드의 “HiperZones not enforced”는 실제 제품 구성 확인으로 단정하지 않는다. |
| Wall 미지원 | Zones 응답 | 별도 Wall 목록이 제공되지 않았다고 표시한다. Zone들을 가상의 Wall로 묶지 않는다. |

원본 parseWallsXml은 이름과 달리 Zone을 해석하고, parseWallsResponseXml은 Walls 모드를 확인할 뿐 Wall 항목을 반환하지 않는다.
zone-inventory.js는 실제 응답에서 얻은 Zone ID 집합과 별도 프로필의 target.zoneId를 대조하는 검증 함수다.
프로필의 Zone 매핑·CONTENT_NAME은 Controller에서 조회한 목록이 아니다. 이번 구현에서는 로컬 설정으로 목록을 합성하지 않는다.

UUID/Zone ID는 **현재 응답이 공급한 식별자**로 보존한다. 해당 버전에서 수명 동안 안정적인지 공식 명세와 실제 Controller 대조가 필요하다.
이름으로 대상을 확정하거나 누락 ID를 생성하지 않는다. 동일 응답에서 식별자가 중복되면 모호한 목록으로 받아들이지 않고 형식 오류로 처리한다.
당시 UI는 상세 조회만 제공했다. 2026-09-14부터 위 편집 명령을 제공한다.

## 2026-09-10 열린 인스턴스 조회·좌표 표시 확장

추가 근거는 사용자가 지정한 C:/Users/노주형/Desktop/verkada/backend/src/hiperwall.js의
buildListActionXml, getHiperwallWallEditorInstances, parseWallEditorInstancesXml과
frontend/wall-editor-geometry.js, events.js다. **참고 코드의 의미를 확인했으며 공식 상세 명세나
실제 Controller 응답 확인은 아니다.** 원본의 정규식 XML 파서나 버전 추정은 이식하지 않았다.

새로 고침·연결 테스트는 /hello → list → walls → list/filter=open 순서로 읽는다.
추가 요청은 아래 고정 action 하나이며 기존 Token auth를 필요 시 같은 Commands에 넣는다.

```xml
<Commands><action type="list"><filter>open</filter></action></Commands>
```

open은 list의 조회 필터 값이다. action type=open 또는 콘텐츠 쓰기 명령은 전송하지 않는다.
필터 없는 기본 목록을 반환하면 Object에 Instance가 없어 미지원으로 판정하며, 빈 Objects만 성공·빈 인스턴스 목록이다.
HTTP 오류, XML Error, 크기·깊이·DTD 제한은 기존 읽기와 같다. 전체 4개 요청은 같은 제한시간을 공유한다.

| 응답/화면 | 해석 |
|---|---|
| Objects/Object | name, uuid, type, label, zone을 실제 존재할 때 content.* 필드로 보존한다. Object의 zone을 인스턴스 소속으로 자동 확정하지 않는다. |
| Object/Instance | id, position, size, rotation, transparency, layer, showlabel, borderRGB, bordervis, audio를 실제 있을 때 보존한다. Instance ID 누락·동일 응답 중복은 형식 미지원이다. |
| position / size | 쉼표로 구분한 유한수 두 개. size는 양수. 원문을 보존하며 참고 규약에서 position은 중심 X/Y, 앱 Y는 wire Y의 부호 반전이다. |
| 캔버스 | Zone의 left/top/width/height 및 Instance의 position/size 사각 영역을 그린다. 별도 저장 배치나 nearest-Zone 계산은 사용하지 않는다. |
| 표시 제한 | 좌표 미제공, 0이 아닌 회전, ±10억 px 표시 범위 초과는 도형을 제외하고 목록 상세에 이유를 표시한다. 기본 XML의 잘못된 숫자는 형식 미지원이다. |
| layer / audio | layer가 있으면 도형 겹침 순서에 사용한다. audio는 원문 상세만 표시하고 소리·음소거를 제어하지 않는다. |
| 선택 | 목록 종류와 원래 항목 객체/ID를 유지한다. Contents UUID와 인스턴스 ID를 서로 대체하지 않는다. |
| 수명 | 실패·새로 고침·설정 변경 시 이전 인스턴스와 도형을 제거한다. 조회 시각은 목록별이며 자동 주기 갱신·영상 프리뷰는 없다. |

버전별 미확인: 기본 list/open 필터 지원 여부, Instance 필드 구성, position Y/중심 규약, 회전·레이어 의미,
인스턴스 ID 수명, Object/zone의 의미, 해당 계정의 외부 앱이 연 콘텐츠 조회 권한.
원본의 enhanced fields 지원 추정은 채택하지 않았다. 이 확장에서는 실제 Controller 요청을 실행하지 않았다.

## 호스트 저장·권한·수명

- 설정은 기존 지정 데이터 폴더의 control.sqlite HostState.Hiperwall에 추가한다. DB를 초기화하거나 별도 빈 DB를 만들지 않는다.
- 토큰 원문은 저장 API의 요청에만 들어간다. 새 토큰마다 같은 폴더의 hiperwall-<무작위 GUID>.dpapi에
  호스트 Windows 실행 계정의 DPAPI CurrentUser로 보호하여 먼저 저장하고 DB에는 참조만 커밋한다.
- 일반 설정·상태·감사 API는 토큰과 보호 저장 참조를 반환하지 않는다. 편집 화면에 저장된 토큰을 복원하지 않는다.
  실패한 DB 커밋이나 토큰 교체로 남은 이전 보호 파일은 자동 삭제하지 않는다. 이관·참조 없는 파일 정리 도구는 후속 운영 범위다.
  Windows 실행 계정 변경 시 자동 복호화 이관을 제공하지 않는다.
- 연결 변경은 Administrator + 현재 세션 사용권 + 세대 + 설정 버전을 검사한다.
  테스트는 Administrator, 조회는 기존 정책상 모든 활성 인증 계정(Administrator/Operator/Viewer)에 허용한다.
  기존 DeviceIds는 장비 **제어 범위**이고 조회 ACL이 아니므로 Hiperwall 조회 범위로 재해석하지 않는다.
- 조회 시작과 반환 직전에 인증·차단·설정 버전을 재검사한다. 같은 세션의 중복 요청은 409로 거부한다.
  서로 다른 세션의 조회가 겹쳐도 오래된 완료가 호스트의 최신 시작 요청 상태를 덮지 않는다.
  설정 변경은 진행 조회를 취소하고 새 버전만 인정한다.
- WPF는 비동기 조회를 일반 장비/생존 확인 루프와 분리한다. 설정 버전은 기존 /api/state에서 관찰하고
  변경·로그아웃·연결 상실·창 종료 시 요청을 취소하며 응답 적용 세대도 검사한다.
- 실패·재조회 때 이전 항목을 비우고 마지막 성공 시각과 오류를 구분한다. 목록과 성공 시각은 런타임 상태이며
  호스트 재시작 후에는 저장된 설정만 복구하고 “연결 확인 전”으로 시작한다.
- 설정 변경과 사용자의 명시적인 테스트 결과만 한국어 감사 이벤트로 저장한다. 일반 반복 조회는 감사 기록을 추가하지 않는다.
  원문 XML·토큰·인증 응답·네트워크 예외 원문을 로그에 쓰지 않는다.

## 현장 확인 절차와 남은 항목

1. HiperController/HiperInterface 정확한 버전·API 매뉴얼·가동 상태를 확인한다.
2. 호스트 PC를 기준으로 Controller 주소를 확인한다. 127.0.0.1은 **ControlHost가 실행되는 PC**이므로
   앱 PC가 따로 있으면 앱 PC의 로컬 주소를 뜻하지 않는다.
3. 앱에서 실제 URL·인증 방식·사용자·토큰을 입력하여 저장한 후 “적용 설정 연결 테스트”를 실행한다.
4. /hello 제품·버전·인증·역할, list와 walls의 읽기 전용 의미·실제 응답 구조를 매뉴얼과 대조한다.
   쓰기 기능으로 연결을 시험하지 않는다. 오류 원문을 공유할 필요가 있으면 토큰·사용자·민감한 응답을 현장에서 제거한다.
5. Wall/Zone 모드, 빈 목록 의미, 조회 계정 권한, UUID가 없는 콘텐츠와 중복 이름을 확인한다.
   비어 있지 않은 Wall 목록을 구현하려면 해당 버전의 Wall 필드·ID 명세와 정제된 예제가 필요하다.
6. 실제 연결 끊김·잘못된 토큰·제한시간·재접속과 다른 PC의 조회를 확인한다.
   실제 장비 성공 기록은 별도로 작성한다. 이번 가짜 서버 증거를 재사용하지 않는다.

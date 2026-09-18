# 문서 목록

현재 작업은 [프로젝트 기준](PROJECT_GUIDE.md)에서 시작한다. 사용자 최신 지시가 저장된 문서보다 우선한다.

| 목적 | 문서 |
|---|---|
| 설치·서버 실행 확인·계정·제어·장비 슬롯·복구 | [운영 절차](../OPERATIONS.md) |
| 현재 범위·정책·구현 수준·남은 검증 | [프로젝트 기준](PROJECT_GUIDE.md) |
| 화면·공통 입력·테마 | [화면 디자인](UI_DESIGN.md) |
| 기능별 화면 상태·계약 | [ViewModel 안내](FEATURE_VIEWMODELS.md) |
| 서비스 상태·공통 트랜잭션 | [상태 접근 안내](SERVICE_STATE_BOUNDARIES.md) |
| 장비 등록·공유 연결·PC·역할·권한 | [장비 설정 안내](DEVICE_CONFIGURATION.md) |
| 장비 모델·드라이버·통신 | [드라이버 안내](DEVICE_DRIVERS.md) |
| OS·보호 저장·저장소·영상 어댑터 | [환경 어댑터 안내](ENVIRONMENT_ADAPTERS.md) |
| Hiperwall 조회·편집 요청 근거 | [프로토콜 기록](HIPERWALL_PROTOCOL.md) |
| Hiperwall 검증·현장 확인 범위 | [Hiperwall 검증](HIPERWALL_VALIDATION.md) |
| 카메라·MediaMTX·LibVLC·프리뷰 검증 | [영상 검증](MEDIA_VALIDATION.md) |
| 외부 라이브러리 고지·라이선스 | [고지](../THIRD_PARTY_NOTICES.md) |

## 과거 기록과 참고 원본

- [구현·검증·배포 이력](history/IMPLEMENTATION_HISTORY.md): 날짜별 작업 당시의 결과와 판단 근거.
- [첫 구현 검증 기록](history/FIRST_IMPLEMENTATION_VALIDATION.md): 2026-09-09 가상 장비·교대 구현의 초기 검증.
- [작업 공간 보관 자료](../../archive/README.md): 후속 기능·프로토콜 대조에 필요한 이전 웹/Node 코드와 테스트. 별도 clone에는 없을 수 있다.

과거 기록의 “현재/최신”·배포 경로·미구현 항목은 기록 시점 기준이다. 현재 배포는 작업 공간 바로가기와
`artifacts/publish/publish-result.json`에서 확인한다. 로컬 검증 로그는 `artifacts/`에 있으며 Git에 포함하지 않는다.

## 갱신 기준

현재 정책·완료 범위는 프로젝트 기준에, 사용 방법은 운영 절차에, 상세 구현·검증·배포 결과는 이력에 작성한다.
새 문서를 추가하거나 이동하면 이 목록과 상대 링크를 갱신한다. 문서는 기본적으로 Git 추적 대상이다.
실장비 검증과 격리 테스트를 구분하고 비밀값·운영 DB·로그를 문서에 복사하지 않는다.

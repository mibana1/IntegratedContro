# 장비 기능·드라이버·통신 책임

운영 화면과 시나리오는 역할 ID와 `DeviceOperation.Power` 같은 기능을 사용한다.
전원 장비를 다른 제조사·통신 방식으로 바꿀 때는 등록 드라이버와 장비 설정을 변경한다.
제품에 등록된 드라이버는 현재 `virtual`뿐이다. 실제 제조사 프로토콜은 명세와 장비를 확보한 뒤 추가한다.

| 책임 | 구현 | 변경 이유 |
|---|---|---|
| 기능 계약 | Core의 `Capability`, `DeviceModel` | 기능·값 범위·단위·조회 가능 여부. 제조사 명령은 포함하지 않음 |
| 역할·작업 정책 | Application의 `ControlService` | 권한, 사용권, 역할 해석, 기능 검사, 예약, snapshot, 취소·복구 |
| 드라이버 선택 | `DeviceDriverRegistry`, ControlHost 구성 지점 | 명시적으로 등록한 ID·버전과 모델로 선택. 알 수 없는 모델이나 통신 방식은 거절 |
| 명령·결과 계약 | Application의 `DeviceCommand`, `DriverResult`/`DriverStatus` | 대상·기능·값·단위와 장비 결과만 전달. 단계 상태 변환은 `DeviceExecutionService`가 담당 |
| 장비 프로토콜 | `IDeviceDriver`, `VirtualDeviceDriver` | 모델 선언, 설정 검사, 기능 명령 변환, 응답 해석, 장비 제약 |
| 입출력 | `IVirtualDeviceTransport`, `SqliteVirtualDeviceTransport` | 가상 값 저장·조회. 드라이버가 SQLite를 직접 호출하지 않음 |
| 현장 설정 | `DeviceConfig`, `DeviceConnection`, 관리자 장비 설정 화면 | 드라이버, 모델, 연결 ID, 통신 방식, 연결 주소, 장비 주소·채널, 통신/드라이버 옵션 |

## 장비 교체

1. 새 모델의 드라이버가 호스트에 등록되어 있어야 한다. UI에는 호스트가 제공한 모델·통신 방식만 표시한다.
2. 기존 장비 ID의 모델·연결 설정을 수정하거나, 새 장비를 등록하고 기존 역할 ID에 배정한다.
3. 역할을 사용하는 저장 시나리오의 기능·값 범위·조회 조건을 검사한다. 전원만 필요한 역할은 같은 전원 계약을 지원하는 모델로 교체할 수 있다. 밝기가 필요한 역할을 전원 전용 모델로 바꾸면 저장을 거절한다.
4. 변경 후 새로 시작하는 명령·시나리오는 새 드라이버와 설정으로 실행한다. 기존에 접수된 작업은 자동으로 새 대상으로 옮기지 않는다.

장비 이름·PC 표시 이름만 바뀌면 실행 버전을 유지한다. 드라이버·모델·통신 방식·주소·옵션 등이 바뀌면 실행 버전이 증가한다.
접수 시 모델 기능과 드라이버 버전도 고정하며, 전송 직전에 다시 비교한다. 호스트를 재시작하면서 드라이버 버전이나 기능이 바뀌어도 이전 명령을 그대로 실행하지 않는다.
전송 중 교체한 장비의 늦은 결과는 원래 작업에 남기고 새 대상 상태에 적용하지 않는다.

기존 가상 장비 JSON은 `virtual` 드라이버와 통신 방식으로 읽는다. 기존 연결 ID, 장비 ID, 역할, 시나리오를 유지하며 별도 DB 이동은 필요하지 않다.
새 설정을 저장한 뒤 구버전 호스트로 되돌리는 동작은 지원하지 않는다. App과 ControlHost를 함께 갱신한다.

## 새 제조사 드라이버 추가

1. Infrastructure에 `IDeviceDriver` 구현을 추가한다. `Id`, `Version`, 모델별 기능과 `TransportIds`, `IsSimulation`을 선언한다. 프로토콜이나 실행 의미가 바뀌면 드라이버 버전을 올린다.
2. `ValidateConfiguration`에서 해당 제조사가 허용하는 주소·채널·옵션·조합만 허용한다. 이 검사는 입출력 없이 실행해야 한다. 임의 설정을 장비 명령으로 그대로 실행하지 않는다.
3. 드라이버에는 생성자로 전용 통신 구현을 주입한다. Serial/TCP의 스트림·프레이밍과 HTTP 요청을 하나의 가상 바이트 API로 합치지 않는다. 연결 수명·공유 연결 직렬화·제한시간·취소는 해당 통신 구현에서 책임진다. 현재 호스트는 장비 쓰기 전체를 순차 실행하며 자동 재전송하지 않는다.
4. `ExecuteAsync(DeviceCommand, CancellationToken)`에서 대상·기능·값·단위를 해당 프로토콜로 변환하고 `DriverResult`를 반환한다. 예열·냉각 등 장비 제약도 드라이버가 처리한다. 조회할 수 없는 기능에는 `CanRead = false`를 선언한다.
5. ControlHost의 `DeviceDriverRegistry` 생성 지점에 드라이버·통신 구현을 등록한다. 동적 DLL 로딩이나 플러그인 검색은 없다.
6. 관리자 화면에서 연결 정보를 설정하고 같은 역할을 사용한다. 새 기능 종류 자체를 추가하는 경우에는 공통 계약·UI 변경도 필요하다.

옵션은 비밀이 아닌 설정만 담는다. 비밀번호·토큰은 이 옵션이나 연결 URL에 넣지 않으며, 인증이 필요한 실제 드라이버 구현 시 보호 저장 참조를 사용해야 한다.
정해지지 않은 제조사 명령, Serial 설정, TCP 포트, HTTP 경로를 추정해 구현하지 않는다.

## 장비 명령과 작업 모델의 경계

`IDeviceDriver`, `DeviceCommand`, `DriverStatus`, `DriverResult`, `DriverReading`은
`src/IntegratedContro.Application/DeviceDriverContracts.cs`에 모여 있다.
드라이버는 `StepSnapshot`이나 `StepStatus`를 입력·결과로 사용하지 않는다.

`DeviceExecutionService`가 접수된 단계의 고정 대상 설정을 깊은 복사해
`DeviceCommand(Target, Operation, Value, Unit)`를 만든다. 역할, 단계 종류, 지연,
사전 조건, 실패 정책, 영상벽 배치와 시나리오 진행 정보는 장비에 전달하지 않는다.
단계 제한시간과 호스트 종료는 실행 계층에서 취소 토큰으로 전달한다.
명령의 연결/드라이버 옵션을 변경해도 접수된 작업이나 현재 장비 설정은 바뀌지 않는다.

드라이버의 `DriverStatus`는 가상 반영·전송·ACK·관측·실패·불확실 결과만 표현한다.
실행 서비스가 이를 내부 `StepExecutionResult`의 `StepStatus`로 명시적으로 변환하고,
가상/물리 모드와 증거·관측값을 검증한다. 알 수 없는 결과 상태와 관측 증거 불일치는
`Unknown`으로 처리해 후속 실행을 차단하고 대조를 요구한다.
조건 대기와 영상벽 표시도 내부 단계 결과를 쓰며 드라이버 결과 계약에 의존하지 않는다.

호스트 API와 저장되는 snapshot·단계 상태 형식은 유지하므로 DB 마이그레이션은 없다.
기존 드라이버 구현은 새 명령 입력과 `DriverStatus` 결과로 다시 빌드해야 한다.

## 결과와 관측

- `Simulated` + `Simulation`: 가상 값. `DeviceState.Simulated`에만 기록한다.
- `Sent` / `Acknowledged`: 전송 완료 / 프로토콜 응답. 반환 값이 있어도 실제 상태로 저장하지 않는다.
- `Observed` + `Observed`: 드라이버가 실제 조회·응답 해석으로 확인한 값. `DeviceState.Observed`에 기록한다.
- 조건 검사와 대조에는 해당 모드에 맞는 상태 증거가 필요하다. ACK나 전송 완료로 조건을 충족시키지 않는다.
- 시간 초과·결과 유실은 불확실 상태이며 자동 재전송하지 않는다. 관측 실패를 전원 꺼짐으로 바꾸지 않는다.

테스트의 두 제조사 드라이버는 서로 다른 전원 명령 형식을 가짜 전송기에 기록한다. 실제 Serial/TCP/HTTP 장비를 제어하거나 검증한 결과는 아니다.

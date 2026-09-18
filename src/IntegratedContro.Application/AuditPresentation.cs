using IntegratedContro.Core;

namespace IntegratedContro.Application;

/// <summary>Adds readable, captured display fields while retaining every original audit value.</summary>
public static class AuditPresentation
{
    public static AuditEntry Enrich(AuditEntry entry, HostState state)
    {
        if (entry.Message is not null && entry.EventName is not null && entry.UserName is not null) return entry;
        var fields = entry.Detail.Split(';', StringSplitOptions.TrimEntries)
            .Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .GroupBy(x => x[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        string Field(string key) => fields.GetValueOrDefault(key, "");
        Guid Id(string key) => Guid.TryParse(Field(key), out var id) ? id : Guid.Empty;
        string Short(Guid id) => id == Guid.Empty ? "알 수 없음" : id.ToString()[..8];
        var job = state.Jobs.SingleOrDefault(j => j.Id == Id("job"));
        var device = state.Devices.SingleOrDefault(d => d.Id == Id("device"));
        var account = state.Accounts.SingleOrDefault(a => a.Id == Id("account"));
        var scenario = state.Scenarios.SingleOrDefault(s => s.Id == Id("scenario"));
        var actor = entry.UserId is null ? "호스트" : state.Accounts.SingleOrDefault(a => a.Id == entry.UserId)?.Name ?? $"계정 {Short(entry.UserId.Value)}";
        var title = entry.Action switch
        {
            "CameraSaved" => "카메라 등록 저장", "CameraSyncRequested" => "카메라 경로 재동기화 접수",
            "CameraDeleteRequested" => "카메라 삭제 접수", "CameraForceDeleted" => "카메라 강제 삭제",
            "CameraDeleted" => "카메라 삭제 완료", "CameraPathCleaned" => "영상 경로 정리 완료",
            "CameraReconciled" => "영상 경로 준비 결과", "CameraCleanupRequested" => "영상 경로 정리 재시도",
            "MediaSettingsSaved" => "MediaMTX 연결 설정 변경",
            "InitialAdministratorCreated" => "최초 관리자 생성", "HostStarted" => "호스트 시작",
            "Login" => "로그인", "Acquire" => "사용 시작", "ReleasePreservingJobs" => "사용 종료",
            "LogoutPreservingJobs" => "로그아웃", "ConnectionLost" => "연결 이상",
            "PreviousSessionFenced" => "이전 세션 차단", "AccountCreated" => "계정 등록",
            "AccountPermissionsChanged" => "계정 권한 변경", "VirtualDeviceSaved" => "가상 장비 저장", "DeviceSaved" => "장비 설정 저장",
            "RoleAssigned" => "역할 배정", "RoleUnassigned" => "역할 배정 해제",
            "RoleRenamed" => "역할 이름 수정", "RoleDeleted" => "역할 삭제", "ScenarioSaved" => "시나리오 저장", "ScenarioDeleted" => "시나리오 삭제",
            "HiperwallSlotSaved" => "Hiperwall 슬롯 저장", "HiperwallSlotDeleted" => "Hiperwall 슬롯 삭제",
            "HiperwallSettingsSaved" => "Hiperwall 연결 설정 변경", "HiperwallConnectionTest" => "Hiperwall 연결 테스트",
            "HiperwallEditAccepted" => "Hiperwall 편집 접수", "HiperwallEditResult" => "Hiperwall 전송 결과", "HiperwallEditCancelled" => "Hiperwall 미전송 취소",
            "HiperwallLayoutSaved" => "영상벽 배치 저장", "HiperwallLayoutDeleted" => "영상벽 배치 삭제",
            "HiperwallDisplayAccepted" => "영상벽 표시 접수", "HiperwallDisplayStop" => "영상벽 표시 종료 요청",
            "HiperwallDisplayCleanup" => "영상벽 인스턴스 정리",
            "LightSlotSaved" => "장비 슬롯 저장", "LightSlotDeleted" => "장비 슬롯 삭제", "LightSlotRestored" => "장비 슬롯 불러오기",
            "LightOrderSaved" => "조명 배치 저장", "LightBatchAccepted" => "조명 일괄 명령 접수",
            "JobAccepted" => "작업 접수", "JobCancellation" => "작업 취소 요청",
            "VirtualStateReconciled" => "가상 상태 대조", "DeviceStateReconciled" => "장비 상태 대조", "DispatchIntent" => "전송 준비",
            "DispatchResult" => "전송 결과", "DispatchRejected" => "전송 차단",
            "RecoveryReviewed" => "복구 작업 확인", "RecoveryApproved" => "복구 인계 승인",
            _ => "기타 기록"
        };
        var deviceName = device is null ? $"장비 {Short(Id("device"))}" : $"{device.Name} / {device.PcName}";
        string Step(StepSnapshot s) => $"{s.KindLabel} · {s.TargetLabel}" + (s.Kind == ScenarioStepKind.DisplayLayout ? "" : $": {Operation(s.Operation, s.Value, s.Unit)}");
        var work = job is null ? $"작업 {Short(Id("job"))}" :
            $"{job.Snapshot.RequesterName} 요청 · " + (job.IsDeviceBatch || job.Kind == JobKind.Scenario
                ? $"{job.Snapshot.Name} · {job.Snapshot.Steps.Length}단계"
                : Step(job.Snapshot.Steps[0]));
        var step = int.TryParse(Field("step"), out var index) && job is not null &&
            index >= 0 && index < job.Snapshot.Steps.Length ? job.Snapshot.Steps[index] : null;
        var stage = step is null ? work : $"{index + 1}단계 · {Step(step)}";
        string State(string value) => value switch
        {
            "Simulated" => "가상 실행 완료", "Unknown" => "결과 불확실 · 상태 대조 필요",
            "Waiting" => "조건·표시 응답 대기", "ConditionMet" => "조건 충족", "Acknowledged" => "프로토콜 응답 확인", "Sent" => "전송 완료", "Observed" => "장비 상태 관측",
            "Failed" => "실패", "Skipped" => "미전송 · 차단", "Dispatching" => "전송 결과 대기",
            _ => "결과 확인 필요"
        };
        var message = entry.Action switch
        {
            "HiperwallSettingsSaved" => $"Hiperwall 연결 설정 버전 {Field("version")}을 저장했습니다. 새 연결 확인이 필요합니다.",
            "HiperwallConnectionTest" => $"적용 설정 버전 {Field("version")}의 읽기 전용 연결 테스트: {(Enum.TryParse<HiperwallConnectionState>(Field("result"), out var status) ? HiperwallLabels.State(status) : "결과 확인 필요")}.",
            "HiperwallEditAccepted" or "HiperwallEditResult" or "HiperwallEditCancelled" => entry.Detail,
            "HiperwallLayoutSaved" => $"배치 버전 {Field("version")}을 저장했습니다. LIVE는 변경하지 않았습니다.",
            "HiperwallLayoutDeleted" => "저장 배치를 삭제했습니다. 이미 접수한 표시는 원래 배치를 유지합니다.",
            "HiperwallDisplayAccepted" => $"표시 작업 {Short(Id("request"))}을 접수하고 대상 {Field("targets")}개와 종료 일정을 저장했습니다.",
            "HiperwallDisplayStop" => $"표시 작업 {Short(Id("request"))}의 미전송 부분 중단과 열린 표시 정리를 요청했습니다.",
            "HiperwallDisplayCleanup" => $"표시 작업 {Short(Id("request"))}: {(Field("state") == "Closed" ? "인스턴스 없음 또는 미전송 종료 확인" : "자동 정리 중단 · 확인 필요")}.",
            "InitialAdministratorCreated" => "호스트 최초 설정과 관리자 생성을 완료했습니다.",
            "HostStarted" => "호스트를 시작하고 작업을 복구했습니다. 불확실 명령과 중단 시나리오는 자동 재실행하지 않습니다.",
            "Login" => $"{Field("pc")}에서 로그인했습니다.",
            "Acquire" => $"사용권을 획득했습니다. 사용권 세대 {Field("generation")}.",
            "ReleasePreservingJobs" => "사용권을 반납했습니다. 접수한 작업은 호스트에서 계속 처리합니다.",
            "LogoutPreservingJobs" => "로그아웃했습니다. 접수 작업과 원 요청자 기록은 유지됩니다.",
            "ConnectionLost" => entry.Detail,
            "PreviousSessionFenced" => $"이전 세션의 신규 요청을 차단했습니다. 사용권 세대 {Field("generation")}.",
            "AccountCreated" => $"{account?.Name ?? Short(Id("account"))} 계정을 등록했습니다. 권한: {Role(Field("role"))}.",
            "AccountPermissionsChanged" => $"{account?.Name ?? Short(Id("account"))} 계정의 활성 상태·장비 제어 범위를 변경했습니다.",
            "VirtualDeviceSaved" or "DeviceSaved" => $"{deviceName} 설정을 저장했습니다. 설정 버전 {Field("v")}.",
            "RoleAssigned" => $"{Field("role")} 역할을 {deviceName}에 배정했습니다.",
            "RoleUnassigned" => $"{deviceName}의 {Field("role")} 역할 배정을 해제했습니다.",
            "RoleRenamed" => $"{Field("role")} 역할 이름을 수정했습니다. 기존 작업 대상은 유지됩니다.",
            "RoleDeleted" => $"{Field("role")} 역할을 삭제했습니다. 장비와 실행 이력은 유지됩니다.",
            "HiperwallSlotSaved" => $"슬롯 {Field("slot")}에 현재 콘텐츠 {Field("count")}개의 Zone·위치·크기를 저장했습니다. 버전 {Field("version")}.",
            "HiperwallSlotDeleted" => $"슬롯 {Field("slot")}의 저장 정보를 삭제했습니다. 현재 표시는 유지됩니다.",
            "ScenarioSaved" => $"{scenario?.Name ?? Short(Id("scenario"))} 시나리오를 저장했습니다. 버전 {Field("v")}.",
            "ScenarioDeleted" => $"{scenario?.Name ?? Short(Id("scenario"))} 시나리오 정의를 삭제했습니다. 버전 {Field("v")}. 실행 이력은 보존합니다.",
            "LightOrderSaved" => $"조명 {Field("count")}개의 그룹·표시 순서를 저장했습니다. 배치 버전 {Field("version")}.",
            "LightSlotSaved" => $"슬롯 {Field("slot")}에 장비 {Field("count")}개의 그룹·순서·ON/OFF를 저장했습니다. 현재 장비는 유지됩니다.",
            "LightSlotDeleted" => $"슬롯 {Field("slot")}의 저장 정보만 삭제했습니다.",
            "LightSlotRestored" => $"{work} · 그룹·순서 복원 및 전원 적용 접수. 장비별 실행 결과는 작업 탭에서 확인하세요.",
            "LightBatchAccepted" or "JobAccepted" => $"{work} · 접수 완료, 실행 결과는 별도로 확인하세요.",
            "JobCancellation" => $"{work} · 미전송 부분 취소 요청. 이미 전송한 동작의 정지·롤백은 아닙니다.",
            "VirtualStateReconciled" or "DeviceStateReconciled" => $"{deviceName}의 상태를 대조했습니다. 과거 작업 결과는 변경하지 않았습니다.",
            "DispatchIntent" => $"{stage} · 단계 시작을 기록했습니다. 조건 충족·동작 완료를 뜻하지 않습니다.",
            "DispatchResult" => $"{stage} · {State(Field("result"))}.",
            "DispatchRejected" => $"{work} · {string.Join("; ", entry.Detail.Split(';').Skip(1)).Trim()}",
            "RecoveryReviewed" => "관리자가 진행 중 작업·예약·불확실 장비를 확인했습니다.",
            "RecoveryApproved" => "관리자 복구 인계를 승인했습니다. 기존 작업과 불확실 장비 제한은 유지합니다.",
            _ => entry.Detail
        };
        return entry with { UserName = entry.UserName ?? actor, EventName = entry.EventName ?? title, Message = entry.Message ?? message };
    }
    private static string Role(string role) => role switch
    {
        "Administrator" => "관리자", "Operator" => "운영자", "Viewer" => "조회 전용", _ => role
    };
    public static string Operation(DeviceOperation op, int value, string unit) => op switch
    {
        DeviceOperation.Power => value == 1 ? "전원 ON" : "전원 OFF",
        DeviceOperation.Brightness => $"밝기 {value}{unit}", DeviceOperation.Volume => $"음량 {value}{unit}",
        DeviceOperation.Mute => value == 1 ? "음소거" : "음소거 해제",
        DeviceOperation.Input => $"입력 {value}", DeviceOperation.Lift => $"승강 {value}{unit}",
        DeviceOperation.Stop => "정지 요청", _ => $"{op} {value}{unit}"
    };
}

using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed partial class MainViewModel
{
    private SessionInfo? MySession => IsLoggedIn ? _state?.Session ?? _login?.Session : null;
    public string MyAccountName => MySession?.UserName ?? "로그인 전";
    public string MyAccountRole => MySession?.Role switch
    {
        AccountRole.Administrator => "관리자",
        AccountRole.Operator => "운영자",
        AccountRole.Viewer => "조회 전용",
        _ => "접속 후 계정 정보를 확인할 수 있습니다."
    };
    public string MyAccountId => MySession?.UserId.ToString() ?? "—";
    public string MyPcName => MySession?.PcName ?? Environment.MachineName;
    public string MyPcId => MySession?.PcId.ToString() ?? "—";
    public string MySessionId => MySession?.Id.ToString() ?? "—";
    public string MyHostEndpoint => string.IsNullOrWhiteSpace(Endpoint) ? "설정되지 않음" : Endpoint;
    public string MyControlScope => !_connected || _state is null ? "호스트에 연결하면 현재 제어 권한을 확인할 수 있습니다." :
        MySession?.Role == AccountRole.Viewer ? "조회 전용 계정입니다." :
        _state.Accounts.FirstOrDefault(a => a.Id == MySession?.UserId) is { AllDevices: true } ? "전체 장비" :
        _state.ControllableDeviceIds.Length == 0 ? "제어 가능한 등록 장비가 없습니다." :
        string.Join(", ", _state.Devices.Where(d => _state.ControllableDeviceIds.Contains(d.Id)).Select(d => d.Name));
    public string MyHiperwallPermission => !_connected || _state is null ? "연결 후 확인" :
        _state.CanControlHiperwall ? "제어 가능 · 사용 시작 후 조작할 수 있습니다." : "제어 권한 없음";
    public string MyControlStatus => !IsLoggedIn ? "로그인 전" : !_connected ? "연결 이상 · 신규 제어 차단" :
        CanControl ? "사용 중" : _state?.Lease.Mode switch
        {
            LeaseMode.Free => "조회 중 · 사용 시작 가능",
            LeaseMode.Held => "다른 세션 사용 중 · 현재 세션은 조회만 가능합니다.",
            LeaseMode.RecoveryRequired => "관리자 복구 인계 필요",
            _ => "상태 확인 중"
        };
    public string MySlotSupport => !IsLoggedIn || !_connected || _state is null ? "호스트 연결 후 확인할 수 있습니다." :
        _state.HiperwallSlotsSupported ? "지원됨" :
        "현재 ControlHost는 저장 슬롯을 지원하지 않습니다. 슬롯 기능이 포함된 ControlHost로 전환해야 합니다.";
    private void NotifyMyInfo()
    {
        foreach (var name in new[] { nameof(MyAccountName), nameof(MyAccountRole), nameof(MyAccountId),
            nameof(MyPcName), nameof(MyPcId), nameof(MySessionId), nameof(MyHostEndpoint), nameof(MyControlScope),
            nameof(MyHiperwallPermission), nameof(MyControlStatus), nameof(MySlotSupport) }) Changed(name);
    }
}

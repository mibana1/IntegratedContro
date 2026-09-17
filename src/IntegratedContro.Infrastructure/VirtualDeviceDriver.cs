using IntegratedContro.Application;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Infrastructure;

/// <summary>Model behavior and simulated faults; persistence is supplied by a separate transport.</summary>
public sealed class VirtualDeviceDriver(IVirtualDeviceTransport transport) : IDeviceDriver
{
    public string Id => "virtual";
    public string Version => "1";
    public DeviceModel[] Models =>
    [
        new("virtual-light", "가상 조명 (조광)", [new(DeviceOperation.Power,0,1,"on/off"), new(DeviceOperation.Brightness,0,100,"%")], DeviceCategory.Lighting),
        new("virtual-light-basic", "가상 조명 (전원만)", [new(DeviceOperation.Power,0,1,"on/off")], DeviceCategory.Lighting),
        new("virtual-projector", "가상 프로젝터", [new(DeviceOperation.Power,0,1,"on/off"), new(DeviceOperation.Input,1,4,"입력 번호")], DeviceCategory.Projection),
        new("virtual-audio", "가상 음향", [new(DeviceOperation.Power,0,1,"on/off"), new(DeviceOperation.Volume,0,100,"%"), new(DeviceOperation.Mute,0,1,"on/off")], DeviceCategory.Audio),
        new("virtual-lift", "가상 승강", [new(DeviceOperation.Lift,-1,1,"-1=하강 / 0=정지 / 1=상승"), new(DeviceOperation.Stop,0,0,"STOP", false)], DeviceCategory.Lift)
    ];
    public void ValidateConfiguration(DeviceConfig device)
    {
        Require(device.DriverId == Id && Models.Any(m => m.Id == device.ModelId),
            "unsupported_model", "가상 드라이버 모델을 확인하세요.", 400);
        Require(device.Connection is { TransportId: "virtual", Endpoint.Length: 0, Address.Length: 0 } &&
            device.Connection.Options.Count == 0 && device.DriverOptions.Count == 0,
            "invalid_connection", "가상 장비에는 물리 통신 주소나 옵션을 지정할 수 없습니다.", 400);
        Require(Enum.IsDefined(device.Fault) && device.LatencyMs is >= 0 and <= 30000,
            "invalid_device", "가상 지연은 0~30000ms입니다.", 400);
    }

    public async Task<DriverResult> ExecuteAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        var target = command.Target;
        ValidateConfiguration(target);
        var capability = Models.Single(m => m.Id == target.ModelId).Capabilities.SingleOrDefault(c => c.Operation == command.Operation);
        Require(capability is not null && command.Unit == capability.Unit && command.Value >= capability.Minimum && command.Value <= capability.Maximum,
            "unsupported", "지원되는 장비 기능과 값을 확인하세요.", 400);
        if (target.Fault == VirtualFault.Disconnected) return new(DriverStatus.Failed, "가상 연결 끊김 / 전송 없음");
        if (target.Fault == VirtualFault.Failure) return new(DriverStatus.Failed, "주입된 가상 실패 / 전송 없음");
        await Task.Delay(target.LatencyMs, cancellationToken);
        if (target.Fault == VirtualFault.NoResponse) await Task.Delay(Timeout.Infinite, cancellationToken);
        var operation = command.Operation == DeviceOperation.Stop ? DeviceOperation.Lift : command.Operation;
        await transport.WriteAsync(target, operation, command.Value, cancellationToken);
        if (target.Fault == VirtualFault.ResponseLost) await Task.Delay(Timeout.Infinite, cancellationToken);
        return new(DriverStatus.Simulated, "가상 장비 값 반영 / 실제 장비 관측 아님",
            new Dictionary<DeviceOperation, int> { [operation] = command.Value });
    }

    public async Task<DriverReading> ReadAsync(DeviceConfig device, CancellationToken cancellationToken)
    {
        ValidateConfiguration(device);
        if (device.Fault is VirtualFault.Disconnected or VirtualFault.NoResponse or VirtualFault.Failure)
            return new(false, new Dictionary<DeviceOperation, int>(), "가상 상태 조회 불가: 연결/주입 장애를 확인하세요.");
        var saved = await transport.ReadAsync(device, cancellationToken);
        var values = Models.Single(m => m.Id == device.ModelId).Capabilities.Where(c => c.CanRead)
            .ToDictionary(c => c.Operation, c => saved.GetValueOrDefault(c.Operation, c.Minimum));
        return new(true, values, "가상 상태 조회 / 실제 장비 관측 아님");
    }
}

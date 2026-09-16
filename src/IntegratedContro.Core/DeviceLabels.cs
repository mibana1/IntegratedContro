namespace IntegratedContro.Core;

public static class DeviceLabels
{
    public static string Operation(DeviceOperation operation) => operation switch
    {
        DeviceOperation.Power => "전원", DeviceOperation.Brightness => "밝기",
        DeviceOperation.Volume => "음량", DeviceOperation.Mute => "음소거",
        DeviceOperation.Input => "입력", DeviceOperation.Lift => "승강",
        DeviceOperation.Stop => "정지", _ => operation.ToString()
    };
    public static string Format(DeviceOperation operation, int value, string unit = "") => operation switch
    {
        DeviceOperation.Power => value == 1 ? "전원 켜기 (ON)" : "전원 끄기 (OFF)",
        DeviceOperation.Mute => value == 1 ? "음소거" : "음소거 해제",
        DeviceOperation.Lift => value switch { -1 => "하강", 0 => "승강 정지", 1 => "상승", _ => $"승강 {value}" },
        DeviceOperation.Stop => "정지 (STOP)",
        _ => $"{Operation(operation)} {value}{unit}"
    };
}

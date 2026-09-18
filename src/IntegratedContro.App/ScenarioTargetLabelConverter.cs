using System.Globalization;
using System.Windows;
using System.Windows.Data;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class ScenarioTargetLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.FirstOrDefault() is not ScenarioStep step) return "";
        if (values.ElementAtOrDefault(1) is not IReadOnlyDictionary<string, string> names) return "대상 확인 필요";
        var key = step.Kind == ScenarioStepKind.DisplayLayout ? $"layout:{step.LayoutId}" : step.RoleId;
        return names.GetValueOrDefault(key, "대상 연결 확인 필요");
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

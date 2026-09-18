using System.Globalization;
using System.Windows.Data;
using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed class ConnectionDescriptionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.FirstOrDefault() is SharedDeviceConnection connection
            ? connection.Describe(values.ElementAtOrDefault(1) as IEnumerable<PcRegistration> ?? [],
                values.ElementAtOrDefault(2) as IEnumerable<DeviceModel> ?? []) : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

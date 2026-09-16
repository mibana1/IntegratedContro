using System.Windows;
using System.Windows.Controls;

namespace IntegratedContro.App;

public partial class PowerButtons : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int?),
        typeof(PowerButtons), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public int? Value { get => (int?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public PowerButtons() => InitializeComponent();
    private void SelectOn(object sender, RoutedEventArgs e) => SetCurrentValue(ValueProperty, 1);
    private void SelectOff(object sender, RoutedEventArgs e) => SetCurrentValue(ValueProperty, 0);
}

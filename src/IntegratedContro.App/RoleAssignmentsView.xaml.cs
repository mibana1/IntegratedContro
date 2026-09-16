using System.Windows;
using System.Windows.Controls;

namespace IntegratedContro.App;

public partial class RoleAssignmentsView : UserControl
{
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool),
        typeof(RoleAssignmentsView), new PropertyMetadata(false));
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public RoleAssignmentsView() => InitializeComponent();
}

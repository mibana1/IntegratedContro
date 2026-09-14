using System.Windows;
using System.Windows.Controls;
namespace IntegratedContro.App;
public partial class HiperwallSettingsView : UserControl
{
    public HiperwallSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            TokenInput.Clear();
            if (e.OldValue is HiperwallViewModel old) { old.ReadSecret = () => ""; old.ClearSecret = () => { }; }
            if (e.NewValue is HiperwallViewModel model) { model.ReadSecret = () => TokenInput.Password; model.ClearSecret = TokenInput.Clear; }
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) TokenInput.Clear(); };
    }
}

using System.Windows.Controls;

namespace DevStrider.Desktop.Views;

/// <summary>
/// This file is not boilerplate. Without it the XAML compiler still emits the partial class and its
/// <c>InitializeComponent</c>, but nothing calls it, so the control instantiates with a null Content
/// and the whole tab renders as an empty white panel — which is exactly what Job Operations did from
/// the day it was added until 9.3.1. It was the only view in the project missing its code-behind.
/// </summary>
public partial class AssistedAutomationView : UserControl
{
    public AssistedAutomationView() => InitializeComponent();
}

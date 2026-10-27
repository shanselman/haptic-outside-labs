namespace Haptics.Lab;

/// <summary>Real accessible controls: their UIA providers, not click handlers, drive the observation pipeline.</summary>
public partial class ShowcaseDemo : System.Windows.Controls.UserControl
{
    public ShowcaseDemo()
    {
        InitializeComponent();
        DemoPassword.Password = "not-a-secret";
    }
}

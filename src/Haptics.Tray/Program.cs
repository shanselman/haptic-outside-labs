using Haptics.Windows;

namespace Haptics.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == UiaWorker.Arg)
        {
            return UiaWorker.Run(args);
        }

        using var single = new Mutex(true, @"Local\HapticsEverywhereTray", out var first);
        if (!first)
        {
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The settings window is WPF (WPF-UI). The WinForms loop below pumps messages for both. The theme dictionaries
        // must be merged before anything asks WPF-UI for the application, or it falls back to a private copy.
        var wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        wpf.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
        wpf.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();

        // WPF handler exceptions would otherwise escape the WinForms loop and kill the tray; WinForms menu items
        // already get a Continue/Quit dialog.
        wpf.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Haptics Everywhere");
            e.Handled = true;
        };

        Application.Run(new TrayContext());
        return 0;
    }
}

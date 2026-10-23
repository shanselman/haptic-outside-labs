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
        Application.Run(new TrayContext());
        return 0;
    }
}

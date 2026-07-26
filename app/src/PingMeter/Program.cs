using PingMeter.App;
using PingMeter.Network;

namespace PingMeter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Elevated repair helper: must run before the single-instance mutex (the main
        // instance is already holding it) and without any UI.
        if (args.Contains(NetworkRepair.HelperArgument, StringComparer.Ordinal))
        {
            NetworkRepair.RunElevatedHelper();
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\PingMeter_SingleInstance", out bool createdNew);
        if (!createdNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        Application.Run(new TrayContext());
    }
}

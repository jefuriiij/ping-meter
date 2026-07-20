using PingMeter.App;

namespace PingMeter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\PingMeter_SingleInstance", out bool createdNew);
        if (!createdNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

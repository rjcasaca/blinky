using System;
using System.Threading;
using System.Windows;

namespace Blinky.App;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, @"Local\Blinky.Recorder", out bool created);
        if (!created)
        {
            MessageBox.Show("Blinky is already running. Open it from the system tray.", "Blinky");
            Shutdown();
            return;
        }
        base.OnStartup(e);
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show("Blinky could not start: " + error.Message + "\n\nUse Windows 10/11 x64 with the Microsoft Visual C++ 2015–2022 x64 runtime installed. Windows N also needs the Media Feature Pack.", "Blinky", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}

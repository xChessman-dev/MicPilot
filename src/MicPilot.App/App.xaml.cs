using System.Threading;
using System.Windows;

namespace MicPilot.App;
public partial class App : Application
{
    private Mutex? _mutex;
    internal static string? DiagnosticDirectory { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--ui-check") DiagnosticDirectory = Path.GetFullPath(e.Args[1]);
        if (DiagnosticDirectory is null)
        {
            _mutex = new Mutex(true, "Local\\MicPilot-AudioConsole", out var created);
            if (!created) { MessageBox.Show("MicPilot уже открыт. Используйте существующее окно.", "MicPilot"); Shutdown(); return; }
        }
        else Directory.CreateDirectory(DiagnosticDirectory);
        DispatcherUnhandledException += (_, args) =>
        {
            if (DiagnosticDirectory is not null) File.WriteAllText(Path.Combine(DiagnosticDirectory, "error.txt"), args.Exception.ToString());
            else MessageBox.Show(args.Exception.Message, "MicPilot — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true; Shutdown(1);
        };
        base.OnStartup(e); var window = new MainWindow();
        if (DiagnosticDirectory is not null) { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -5000; window.Top = 0; window.ShowActivated = false; }
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}

using System.Windows;
using System.Windows.Controls;
using MicPilot.App.Services;
using MicPilot.App.ViewModels;

namespace MicPilot.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    public MainWindow()
    {
        InitializeComponent(); WindowBounds.Attach(this);
        _model = new MainViewModel(App.DiagnosticDirectory is not null); DataContext = _model;
        _model.LevelsChanged += (input, _) => LevelGraph.Add(input); _model.CpuChanged += value => CpuGraph.Add(value);
        Closed += (_, _) => _model.Dispose();
        Loaded += async (_, _) =>
        {
            if (App.DiagnosticDirectory is null) return;
            try { await UiDiagnostics.Run(this, App.DiagnosticDirectory); Application.Current.Shutdown(0); }
            catch (Exception e) { File.WriteAllText(Path.Combine(App.DiagnosticDirectory, "error.txt"), e.ToString()); Application.Current.Shutdown(1); }
        };
    }
    private void Navigation_Checked(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel model && sender is RadioButton radio) model.PageIndex = int.Parse((string)radio.Tag); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

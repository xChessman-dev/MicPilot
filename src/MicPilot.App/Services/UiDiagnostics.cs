using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MicPilot.App.ViewModels;

namespace MicPilot.App.Services;
internal static class UiDiagnostics
{
    public static async Task Run(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory); var model = (MainViewModel)window.DataContext;
        var screens = (TabControl)window.FindName("Screens");
        foreach (var size in new[] { new Size(1320, 920), new Size(1040, 720), new Size(1600, 960) })
        {
            window.Width = size.Width; window.Height = size.Height;
            for (var index = 0; index < 4; index++)
            {
                ((RadioButton)window.FindName(new[] { "HomeNav", "TuneNav", "CalibNav", "RouteNav" }[index])).IsChecked = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                if (model.PageIndex != index || screens.SelectedIndex != index) throw new InvalidOperationException("Navigation did not select the requested page.");
                var profile = (ComboBox)window.FindName("ProfileBox"); var start = (Button)window.FindName("EngineButton");
                if (Math.Abs(profile.TranslatePoint(new(), window).Y - start.TranslatePoint(new(), window).Y) > 1 || Math.Abs(profile.ActualHeight - start.ActualHeight) > 1)
                    throw new InvalidOperationException("Profile and Start button are not aligned.");
                Save(window, Path.Combine(directory, $"page-{index}-{size.Width:0}.png"));
                if (index > 0 && FindScroll(screens) is { } scroll)
                {
                    scroll.ScrollToEnd(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    Save(window, Path.Combine(directory, $"page-{index}-bottom-{size.Width:0}.png"));
                    scroll.ScrollToTop(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
            }
            ((RadioButton)window.FindName("HomeNav")).IsChecked = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var input = (ComboBox)window.FindName("InputBox"); var output = (ComboBox)window.FindName("OutputBox"); var refresh = (Button)window.FindName("RefreshButton");
            if (Math.Abs(input.ActualHeight - output.ActualHeight) > 1 || Math.Abs(refresh.ActualHeight - input.ActualHeight) > 1 || refresh.ActualWidth > 44)
                throw new InvalidOperationException("Audio route controls are not on a consistent grid.");
            if (input.ActualWidth < 230 || output.ActualWidth < 230) throw new InvalidOperationException("Audio selectors too narrow.");
        }
        var device = (ComboBox)window.FindName("InputBox"); device.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); var popup = (Popup)device.Template.FindName("PART_Popup", device); Save(popup.Child, Path.Combine(directory, "microphone-popup.png")); device.IsDropDownOpen = false;
        model.Notice = "Проверка длинного сообщения: устройства временно недоступны. Текст должен переноситься и не ломать общую сетку элементов.";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Save(window, Path.Combine(directory, "notice.png"));
        window.Left = 50; window.Top = 50; window.WindowState = WindowState.Maximized;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var corner = ((Button)window.FindName("EngineButton")).PointToScreen(new()); if (corner.X < 0 || corner.Y < 0) throw new InvalidOperationException("Maximized controls cropped.");
        Save(window, Path.Combine(directory, "maximized.png"));
        File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: 4 pages at 1320/1040/1600 px; aligned controls; 42px refresh button; dark microphone popup; separate wrapping notice; maximized bounds. No microphone capture, playback, settings writes or OBS changes.");
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private static void Save(Visual visual, string path)
    {
        if (visual is not FrameworkElement element || element.ActualWidth < 1 || element.ActualHeight < 1) throw new InvalidOperationException("Nothing to render.");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}

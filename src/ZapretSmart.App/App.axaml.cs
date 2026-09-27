using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZapretSmart.App.ViewModels;
using ZapretSmart.App.Views;

namespace ZapretSmart.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(AppPaths.Default);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.Exit += (_, _) => vm.Dispose();
            vm.StartBackgroundWork();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

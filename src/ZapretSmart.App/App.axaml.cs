using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using ZapretSmart.App.Theming;
using ZapretSmart.App.ViewModels;
using ZapretSmart.App.Views;

namespace ZapretSmart.App;

public partial class App : Application
{
    private static readonly TimeSpan ListsRefreshInterval = TimeSpan.FromHours(6);

    private DispatcherTimer? _listsTimer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Ресурсы темы нужны до загрузки окна. Выбранную в настройках тему окно применит, получив модель.
        ThemeResources.Apply(this, AppTheme.Default);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(AppPaths.Default);
            var window = new MainWindow { DataContext = vm };
            // Запуск Планировщиком при входе в Windows: окно не показывается, программа сразу в трее. Без главного
            // окна среда его сама не покажет; открыть можно из трея.
            var hidden = desktop.Args?.Contains(Core.Settings.WindowsAutostart.Argument, StringComparer.OrdinalIgnoreCase) == true;
            if (!hidden) desktop.MainWindow = window;
            // Окно, которое ни разу не показывали, закрывать не нужно: выход тогда завершает программу напрямую.
            var everShown = !hidden;
            window.Opened += (_, _) => everShown = true;
            void Exit()
            {
                window.ExitRequested = true;
                if (everShown) window.Close();
                else desktop.Shutdown();
            }
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => vm.Dispose();

            SetUpTray(window, vm, Exit);

            // Закрытие окна при работающем обходе или раздаче прячет его в трей: они продолжают работать.
            window.Closing += (_, e) =>
            {
                if (!window.ExitRequested && (vm.IsRunning || vm.Share.IsEnabled) && vm.CloseToTray)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };
            window.Closed += (_, _) => desktop.Shutdown();
            // Установка другой версии: закрыться по-настоящему, не в трей, чтобы скрипт смог заменить файлы.
            vm.ExitForUpdateRequested += Exit;

            vm.StartBackgroundWork();
            // Обновляются только списки старше суток, так что частый таймер не создаёт лишних загрузок.
            _listsTimer = new DispatcherTimer(ListsRefreshInterval, DispatcherPriority.Background, (_, _) => vm.StartBackgroundWork());
            _listsTimer.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void SetUpTray(MainWindow window, MainWindowViewModel vm, Action exitApp)
    {
        var on = LoadIcon("tray-on.png");
        var off = LoadIcon("tray-off.png");

        var toggle = new NativeMenuItem();
        toggle.Click += async (_, _) =>
        {
            if (vm.StopCommand.CanExecute(null)) await vm.StopCommand.ExecuteAsync(null);
            else if (vm.StartCommand.CanExecute(null)) vm.StartCommand.Execute(null);
        };
        var show = new NativeMenuItem("Показать");
        show.Click += (_, _) => ShowWindow(window);
        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => exitApp();

        var tray = new TrayIcon
        {
            Menu = new NativeMenu { Items = { show, toggle, new NativeMenuItemSeparator(), exit } },
        };
        tray.Clicked += (_, _) => ShowWindow(window);

        void Refresh()
        {
            tray.Icon = vm.IsRunning ? on : off;
            tray.ToolTipText = "Zapret Smart: " + vm.StatusText;
            toggle.Header = vm.IsRunning || vm.IsSwitching ? "Выключить обход" : "Включить обход";
        }
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.IsRunning) or nameof(MainWindowViewModel.StatusText) or nameof(MainWindowViewModel.IsSwitching))
                Refresh();
        };
        Refresh();
        TrayIcon.SetIcons(Current!, [tray]);
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static WindowIcon LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri($"avares://ZapretSmart/Assets/{name}")));
}

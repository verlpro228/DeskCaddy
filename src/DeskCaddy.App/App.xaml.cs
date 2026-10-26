using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DeskCaddy.Windows;

namespace DeskCaddy;

public partial class App : Application
{
    private const string MutexName = "DeskCaddy.SingleInstance";

    /// <summary>第二个实例通过这个命名事件通知第一个实例「用户又点了一次」。</summary>
    private const string ActivateEventName = "DeskCaddy.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private volatile bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 已经有实例在跑：通知它把菜单栏弹出来，然后自己退出。
            // 之前是静默退出，用户双击 exe 后什么都没有发生，会以为程序坏了。
            SignalFirstInstance();
            Shutdown();
            return;
        }

        StartActivationListener();

        // 从 IDE 终端启动时会继承 NODE_OPTIONS（如 CodeBuddy 注入的 --require 钩子），
        // 被本程序启动的 Electron 应用会加载它并崩溃（实测 Typora 报 0xC0000005）。
        // 启动器不应该把开发期钩子传染给用户的应用，这里直接摘掉。
        SanitizeLaunchEnvironment();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);

        // --launcher / --settings 仅用于开发与截图验证：只打开对应窗口，不创建 Dock。
        if (e.Args.Contains("--launcher", StringComparer.OrdinalIgnoreCase))
        {
            var launcher = new LauncherWindow();
            MainWindow = launcher;
            launcher.ShowLauncher();
            return;
        }

        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            var settings = new SettingsWindow();
            MainWindow = settings;
            settings.Show();
            return;
        }

        var window = new DockWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;

        // 唤醒监听线程让它自然退出，否则它会在 WaitOne 上挂到进程结束。
        _activateEvent?.Set();
        _activateEvent?.Dispose();
        _activateEvent = null;

        _mutex?.Dispose();
        _mutex = null;

        base.OnExit(e);
    }

    /// <summary>通知已运行的实例：用户又启动了一次。</summary>
    private static void SignalFirstInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // 拿不到事件就算了，最坏情况就是静默退出。
        }
    }

    /// <summary>后台等第二个实例的信号，收到后把菜单栏带到前台。</summary>
    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateEventName);

        var thread = new Thread(() =>
        {
            while (!_isShuttingDown)
            {
                if (!_activateEvent.WaitOne(500))
                {
                    continue;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is DockWindow dock)
                    {
                        dock.ShowLauncherFromSecondInstance();
                    }
                    else if (MainWindow is { } window)
                    {
                        window.Activate();
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "DeskCaddy.Activation"
        };

        thread.Start();
    }

    private static void SanitizeLaunchEnvironment()
    {
        foreach (var name in new[] { "NODE_OPTIONS", "ELECTRON_RUN_AS_NODE" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(name, null);
            Diagnostics.AppLog.Info($"已清除继承的 {name}={value}（避免影响被启动的应用）");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
    }

    private static void Log(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskCaddy",
                "logs");

            Directory.CreateDirectory(directory);

            var file = Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss}] {exception}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

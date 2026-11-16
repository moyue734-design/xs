using System;
using System.Reflection;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TM.Framework.Appearance.ThemeManagement;
using TM.Framework.User.Security.PasswordProtection;
using TM.Framework.User.Account.PasswordSecurity.Services;
using TM.Framework.User.Account.AccountBinding;
using TM.Framework.User.Account.Login;
using TM.Framework.User.Profile.BasicInfo;
using TM.Framework.User.Services;
using TM.Services.Framework.AI.Core;
using TM.Framework.Common.Services.Factories;
using TM.Framework.Notifications.SystemNotifications.SystemIntegration;
using TM.Framework.SystemSettings.Proxy.Services;

namespace TM
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    [Obfuscation(Feature = "no NecroBit", Exclude = false, ApplyToMembers = true)]
    public partial class App : Application
    {
        static App()
        {
            System.Windows.FrameworkElement.FocusVisualStyleProperty.OverrideMetadata(
                typeof(System.Windows.Controls.Control),
                new System.Windows.FrameworkPropertyMetadata(
                    defaultValue: null,
                    flags: System.Windows.FrameworkPropertyMetadataOptions.None,
                    propertyChangedCallback: null,
                    coerceValueCallback: (_, _) => null));

            EventManager.RegisterClassHandler(typeof(Window),
                Window.PreviewKeyDownEvent,
                new System.Windows.Input.KeyEventHandler((sender, args) =>
                {
                    if (args.SystemKey == System.Windows.Input.Key.LeftAlt ||
                        args.SystemKey == System.Windows.Input.Key.RightAlt ||
                        args.Key == System.Windows.Input.Key.LeftAlt ||
                        args.Key == System.Windows.Input.Key.RightAlt)
                    {
                        args.Handled = true;
                    }
                }));
        }
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleOutputCP(uint wCodePageID);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCP(uint wCodePageID);

        private static void ConfigureConsoleUtf8()
        {
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
            try { SetConsoleOutputCP(65001); } catch { }
            try { SetConsoleCP(65001); } catch { }
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.InputEncoding = System.Text.Encoding.UTF8;
            }
            catch { }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess, int ProcessInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE processInformation, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        private const int StdErrorHandle = -12;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;

        public static bool IsDebugMode { get; private set; }

        private IWindowFactory? _windowFactory;
        private ThemeManager? _themeManager;
        private ServerAuthService? _serverAuthService;
        private AuthTokenManager? _authTokenManager;
        private BasicInfoSettings? _basicInfoSettings;
        private CurrentUserContext? _currentUserContext;
        private AppLockSettings? _appLockSettings;
        private DispatcherTimer? _autoLockTimer;

        private string? _lastExMsg;
        private DateTime _lastExTime = DateTime.MinValue;
        private int _repeatCount;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (!TryAcquireSingleInstance())
            {
                Shutdown();
                return;
            }

            try
            {

                try
                {
                    var proc = Process.GetCurrentProcess();
                    proc.PriorityClass = ProcessPriorityClass.High;
                    proc.PriorityBoostEnabled = true;
                    GCSettings.LatencyMode = GCLatencyMode.Interactive;
                }
                catch { }

                try
                {
                    var state = new PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = 1,
                        ControlMask = 0x4,
                        StateMask = 0
                    };
                    SetProcessInformation(Process.GetCurrentProcess().Handle, 4,
                        ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                }
                catch { }

                ConfigureConsoleUtf8();

                IsDebugMode = e.Args.Length > 0 && e.Args[0] == "--debug";

                try
                {
                    if (!IsDebugMode)
                    {
                        var nul = CreateFileW("NUL", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                        if (nul != IntPtr.Zero && nul.ToInt64() != -1)
                        {
                            SetStdHandle(StdErrorHandle, nul);
                        }
                    }
                }
                catch
                {
                }

                try
                {
                    var serviceProvider = await Task.Run(() => DependencyInjection.ConfigureServices());
                    _windowFactory = serviceProvider.GetRequiredService<IWindowFactory>();

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DI] 初始化失败: {ex.Message}");
                    StandardDialog.ShowError("DI初始化失败，请重启应用程序", "DI初始化失败", null);
                    Shutdown();
                    return;
                }

                var preLoginWarmup = Task.WhenAll(
                    Task.Run(async () => { try { var tm = ServiceLocator.Get<ThemeManager>(); await tm.PreloadTransitionSettingsAsync().ConfigureAwait(false); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<ServerAuthService>(); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<ProxyService>(); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<LoginService>(); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<AccountBindingService>(); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<OAuthService>(); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<ApiService>(); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<AuthTokenManager>(); } catch { } })
                );
                _ = Task.Run(() => { try { TM.Framework.Common.Helpers.Utility.IpLocationHelper.GetLocation("1.1.1.1"); } catch { } });
                _ = Task.Run(() => { try { System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TM.Framework.Common.Controls.Markdown.MarkdownStreamViewer).TypeHandle); } catch { } });

                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

                this.DispatcherUnhandledException += (sender, args) =>
                {
                    args.Handled = true;

                    if (args.Exception is OperationCanceledException or TaskCanceledException)
                    {
                        Log($"[App] 操作已取消（静默处理）: {args.Exception.Message}");
                        return;
                    }

                    var now = DateTime.Now;
                    var msg = args.Exception.Message;
                    if (msg == _lastExMsg && (now - _lastExTime).TotalSeconds < 2)
                    {
                        _repeatCount++;
                        if (_repeatCount > 3)
                        {
                            return;
                        }
                    }
                    else
                    {
                        _repeatCount = 1;
                    }
                    _lastExMsg = msg;
                    _lastExTime = now;

                    string errorMsg = $"[App] 未处理异常: {args.Exception}";

                    Log("[App] !!! UI线程未处理异常 !!!");
                    Log(errorMsg);

                    try
                    {
                        StandardDialog.ShowError($"发生未处理异常：{args.Exception.Message}", "错误", null);
                    }
                    catch (Exception dialogEx)
                    {
                        Log($"[App] 错误弹窗本身失败: {dialogEx.Message}");
                    }
                };

                TaskScheduler.UnobservedTaskException += (sender, args) =>
                {
                    Log($"[Task] 后台任务未捕获异常: {args.Exception}");
                    args.SetObserved();
                };

                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                {
                    var ex = args.ExceptionObject as Exception;
                    Log($"[Fatal] AppDomain致命异常(IsTerminating={args.IsTerminating}): {ex}");
                };

                if (IsDebugMode)
                {
                    if (!AttachConsole(-1))
                    {
                        AllocConsole();
                    }
                    ConfigureConsoleUtf8();
                }

                await preLoginWarmup;

                _themeManager = ServiceLocator.Get<ThemeManager>();
                _serverAuthService = ServiceLocator.Get<ServerAuthService>();

                try
                {
                    Log("[启动] 初始化主题系统...");
                    _themeManager!.Initialize();
                    Log($"[主题] 当前主题: {ThemeManager.GetThemeDisplayName(_themeManager!.CurrentTheme)}");
                }
                catch (Exception ex)
                {
                    Log($"[主题] 初始化失败: {ex.Message}");
                }

                Log("[启动] 所有登录前服务已就绪（本地免登录模式）");

                const string localUsername = "local";

                var postLoginWarmup = Task.WhenAll(
                    Task.Run(() => { try { ServiceLocator.Get<BasicInfoSettings>(); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<CurrentUserContext>(); } catch { } }),
                    Task.Run(async () => { try { await ServiceLocator.Get<AppLockSettings>().LoadConfigAsync(); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<AccountSecurityService>(); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<SystemIntegrationSettings>(); } catch { } }),
                    Task.Run(() => { try { ServiceLocator.Get<UIStateCache>(); } catch { } })
                );

                await postLoginWarmup;

                try
                {
                    _authTokenManager = ServiceLocator.Get<AuthTokenManager>();
                    _basicInfoSettings = ServiceLocator.Get<BasicInfoSettings>();
                    _currentUserContext = ServiceLocator.Get<CurrentUserContext>();
                    _appLockSettings = ServiceLocator.Get<AppLockSettings>();
                }
                catch (Exception ex)
                {
                    Log($"[DI] 延迟服务解析失败: {ex.Message}");
                }

                try
                {
                    AuthStartupService.Initialize();
                }
                catch (Exception ex)
                {
                    Log($"[启动] AuthStartupService 初始化失败: {ex.Message}");
                }

                try
                {
                    if (_basicInfoSettings != null)
                    {
                        await _basicInfoSettings.EnsureProfileExistsAsync(localUsername);
                        _basicInfoSettings.SwitchUser(localUsername);
                    }
                    _currentUserContext?.Refresh();
                }
                catch (Exception ex)
                {
                    Log($"[启动] 初始化本地用户资料失败: {ex.Message}");
                }

                var bootstrapManager = CreateBootstrapTasks(e.Args);

                Log("[启动] 显示启动进度窗口...");
                var splashWindow = _windowFactory!.CreateWindow<SplashWindow>(bootstrapManager);
                var splashResult = splashWindow.ShowDialog();

                if (splashResult != true)
                {
                    Log("[启动] 启动失败，程序退出");
                    Shutdown();
                    return;
                }

                Log("[启动] 所有任务完成，显示主窗口...");

                var mainWindow = _windowFactory!.CreateWindow<MainWindow>();
                MainWindow = mainWindow;
                ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
                mainWindow.Show();

                if (e.Args.Length > 0)
                {
                    await ProcessCommandLineArgsAsync(e.Args);
                }

                Log("[启动] 程序启动完成");
            }
            catch (Exception ex)
            {
                Log($"[启动] 启动流程异常: {ex.Message}");
                Shutdown();
            }
        }

        private Task ReturnToLogin(string message)
        {
            Log($"[登录] 本地免登录模式，已忽略返回登录请求: {message}");
            return Task.CompletedTask;
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try
            {
                try
                {
                    if (_autoLockTimer != null)
                    {
                        _autoLockTimer.Stop();
                        _autoLockTimer = null;
                        Log("[AppLock] 自动锁定定时器已停止");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[AppLock] 停止定时器失败: {ex.Message}");
                }

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(35));
                try { await RunExitCleanupAsync(cts.Token); }
                catch (OperationCanceledException) { Log("[退出] 清理超时，强制退出"); }
            }
            catch (Exception ex)
            {
                Log($"[退出] 退出流程异常: {ex.Message}");
            }
            finally
            {
                try { ReleaseSingleInstance(); }
                catch (Exception ex) { Log($"[SingleInstance] 释放异常: {ex.Message}"); }

                base.OnExit(e);
            }
        }

    }
}


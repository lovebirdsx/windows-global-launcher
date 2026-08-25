using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CommandLauncher
{
    public class App : Application
    {
        /// <summary>版本号字符串（形如 "1.2.3"），唯一来源是 csproj 的 &lt;Version&gt;，发布时由 CI 按 git tag 覆盖。</summary>
        public static readonly string AppVersionString = ReadAppVersionString();

        /// <summary>版本号（由 <see cref="AppVersionString"/> 解析），用于与 GitHub Release 的 tag 比较。</summary>
        public static readonly Version AppVersion = ParseVersion(AppVersionString);

        public static readonly string AppName = "WindowsCommandLauncher";
        public static readonly string BaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".windows-global-launcher");

        // 保持引用，防止切换器及其键盘钩子被 GC 回收
        private SwitcherWindow? _switcherWindow;

        /// <summary>剪贴板历史窗口（供 WindowActions 的 ShowClipboardHistory 动作唤出）。</summary>
        public static ClipboardWindow? ClipboardHistoryWindow { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // 在最开始注册全局未处理异常处理器，确保初始化期间的异常也能被捕获并记录日志
            RegisterGlobalExceptionHandlers();

            base.OnStartup(e);

            // 确保用户配置目录存在
            if (!Directory.Exists(BaseDir))
            {
                Directory.CreateDirectory(BaseDir);
            }

            Logger.LogInfo("正在创建主窗口");
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            // 用户重复启动（开机自启已起一个、又手动双击）时，第二个实例会广播唤起请求后退出，
            // 这里让已在运行的实例把命令面板弹出来，行为上等同于「双击就能用」。
            SingleInstance.StartListening(mainWindow.ShowWindow);

            Logger.LogInfo("正在创建 Alt+Tab 窗口切换器");
            _switcherWindow = new SwitcherWindow();

            Logger.LogInfo("正在启动剪贴板历史监听");
            ClipboardHistoryManager.Instance.StartListening();
            ClipboardHistoryWindow = new ClipboardWindow();

            // 恢复上次保存的护眼模式（内部会先还原单位矩阵，清理异常退出的颜色残留）
            EyeCareManager.RestoreLastMode();

            // 恢复上次退出时仍打开的贴图（图片贴图与文字便签；整体隐藏状态不记忆，恢复后直接显示）
            PinStore.RestorePins();

            // 清理历史遗留的孤儿 OCR 引擎进程（主程序曾被强杀/崩溃退出时，常驻子进程不会随之退出）
            _ = Task.Run(RapidOcrBackend.KillOrphanedEngines);

            // 启动自动后台下载增强 OCR 引擎（fire-and-forget，合流，不打扰不弹窗）
            if (!RapidOcrBackend.IsInstalled)
            {
                _ = DownloadOcrEngineAsync();

                async Task DownloadOcrEngineAsync()
                {
                    try
                    {
                        bool ok = await OcrEngineInstaller.EnsureInstalledAsync();
                        if (ok)
                            Logger.LogInfo("识图引擎后台下载完成");
                        else
                            Logger.LogWarning("识图引擎后台下载失败（安装器已记详细日志）");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"识图引擎后台下载异常：{ex.Message}");
                    }
                }
            }

            // 启动后台检查更新（fire-and-forget，不打扰：每天最多检查一次，只有发现更高版本才弹窗）
            _ = CheckUpdateAsync();

            async Task CheckUpdateAsync()
            {
                try
                {
                    await UpdateCoordinator.RunStartupCheckAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"启动检查更新异常：{ex.Message}");
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 还原全屏颜色矩阵，避免护眼效果在进程退出后残留
            EyeCareManager.ResetEffect();

            // 贴图状态退出兜底保存：防抖 timer 可能尚未到期，这里停掉并立即落盘
            // （强杀/崩溃时 OnExit 不执行，靠各交互点的防抖保存兜底，见 PinStore）
            PinStore.Flush();

            // 关闭增强 OCR 常驻子进程（幂等；失败不能影响其它退出清理）
            try
            {
                RapidOcrBackend.Shutdown();
            }
            catch (Exception ex)
            {
                Logger.LogError("关闭增强 OCR 子进程失败", ex);
            }

            _switcherWindow?.Dispose();
            ClipboardHistoryManager.Instance.Dispose();
            base.OnExit(e);
        }

        /// <summary>
        /// 读取程序集的 InformationalVersion 作为版本号字符串。
        /// 单文件发布（PublishSingleFile）下 <c>Assembly.Location</c> 为空串，只能走程序集特性而非文件版本信息；
        /// 引入 SourceLink 后该值会带 "+commitHash" 后缀，故按 '+' 截断。
        /// 特性缺失时回退程序集版本，再失败回退 "0.0.0"（不抛异常，版本号读取绝不能影响启动）。
        /// </summary>
        private static string ReadAppVersionString()
        {
            try
            {
                var info = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    int plus = info.IndexOf('+');
                    return plus >= 0 ? info[..plus] : info;
                }

                var assemblyVersion = typeof(App).Assembly.GetName().Version;
                if (assemblyVersion != null)
                    return assemblyVersion.ToString(3);
            }
            catch
            {
                // 读取版本号失败不应影响启动，回退到下面的默认值
            }

            return "0.0.0";
        }

        /// <summary>解析版本号字符串，失败回退 0.0.0（视为「未知版本」，比任何正式版本都小）。</summary>
        private static Version ParseVersion(string text)
        {
            return Version.TryParse(text, out var version) ? version : new Version(0, 0, 0);
        }

        /// <summary>注册全局未处理异常处理器，统一写日志，避免异常静默丢失导致排查困难。</summary>
        private static void RegisterGlobalExceptionHandlers()
        {
            // UI 线程（Dispatcher）未处理异常：记录后标记为已处理，让常驻托盘程序继续存活
            App.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;

            // AppDomain 未处理异常（后台线程等）：无法阻止终止，仅记录日志
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

            // 未观察的 Task 异常：记录后标记为已观察，避免终结器触发时再次抛异常
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        /// <summary>UI 线程未处理异常：记录日志并标记为已处理，避免偶发异常直接杀掉常驻程序。</summary>
        private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                Logger.LogError("UI 线程未处理异常", e.Exception);
            }
            catch
            {
                // 忽略记录日志时自身抛出的错误，避免递归
            }

            e.Handled = true;
        }

        /// <summary>AppDomain 未处理异常：无法阻止进程终止，仅记录日志便于排查。</summary>
        private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var exception = e.ExceptionObject as Exception
                                ?? new Exception($"非 Exception 类型的异常对象: {e.ExceptionObject ?? "null"}");
                Logger.LogError($"AppDomain 未处理异常 (IsTerminating={e.IsTerminating})", exception);
            }
            catch
            {
                // 忽略记录日志时自身抛出的错误，避免递归
            }
        }

        /// <summary>未观察的 Task 异常：记录日志并标记为已观察。</summary>
        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                Logger.LogError("未观察的 Task 异常", e.Exception);
            }
            catch
            {
                // 忽略记录日志时自身抛出的错误，避免递归
            }

            e.SetObserved();
        }
    }
}

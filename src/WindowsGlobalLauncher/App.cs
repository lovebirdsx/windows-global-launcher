using System;
using System.IO;
using System.Windows;

namespace CommandLauncher
{
    public class App : Application
    {
        public static readonly Version AppVersion = new(1, 0, 0, 0);
        public static readonly string AppName = "WindowsCommandLauncher";
        public static readonly string BaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".windows-global-launcher");

        // 保持引用，防止切换器及其键盘钩子被 GC 回收
        private SwitcherWindow? _switcherWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 确保用户配置目录存在
            if (!Directory.Exists(BaseDir))
            {
                Directory.CreateDirectory(BaseDir);
            }

            Logger.LogInfo("正在创建主窗口");
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            Logger.LogInfo("正在创建 Alt+Tab 窗口切换器");
            _switcherWindow = new SwitcherWindow();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _switcherWindow?.Dispose();
            base.OnExit(e);
        }
    }
}

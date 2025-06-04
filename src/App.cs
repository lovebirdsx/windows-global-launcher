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
        }
    }
}

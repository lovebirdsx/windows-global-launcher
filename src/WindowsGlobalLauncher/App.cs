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

        /// <summary>剪贴板历史窗口（供 WindowActions 的 ShowClipboardHistory 动作唤出）。</summary>
        public static ClipboardWindow? ClipboardHistoryWindow { get; private set; }

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

            Logger.LogInfo("正在启动剪贴板历史监听");
            ClipboardHistoryManager.Instance.StartListening();
            ClipboardHistoryWindow = new ClipboardWindow();

            // 恢复上次保存的护眼模式（内部会先还原单位矩阵，清理异常退出的颜色残留）
            EyeCareManager.RestoreLastMode();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 还原全屏颜色矩阵，避免护眼效果在进程退出后残留
            EyeCareManager.ResetEffect();
            _switcherWindow?.Dispose();
            ClipboardHistoryManager.Instance.Dispose();
            base.OnExit(e);
        }
    }
}

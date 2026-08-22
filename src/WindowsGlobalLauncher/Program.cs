using System;
using System.Windows;

namespace CommandLauncher
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                // 命令行参数统一在此解析（配置文件路径、更新重启的 --wait-for-pid）
                StartupArgs.Parse(args);

                Logger.LogInfo("程序启动, 版本: " + App.AppVersionString);

                // 自动更新重启：必须先等旧进程完全退出，再创建窗口/热键/钩子/颜色矩阵，
                // 否则新旧实例会抢同一批全局资源（热键注册失败、双托盘图标、护眼矩阵被旧进程 OnExit 抹掉）
                if (StartupArgs.WaitForPid is int pid)
                    UpdateInstaller.WaitForPreviousInstance(pid);

                // 清理上次更新遗留的 .old 备份与下载临时文件（幂等、静默）
                UpdateInstaller.CleanupLeftovers();

                var app = new App();
                Logger.LogInfo("开始运行应用程序");
                app.Run();
            }
            catch (Exception ex)
            {
                Logger.LogError("程序运行时发生未处理的异常", ex);
                MessageBox.Show($"程序发生错误: {ex.Message}\n\n详细信息已记录到日志文件", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Logger.LogInfo("程序结束");
            }
        }
    }
}

using System;
using System.Windows;

namespace CommandLauncher
{
    class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                Logger.LogInfo("程序启动, 版本: " + App.AppVersion);
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

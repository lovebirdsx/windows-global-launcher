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
                // 命令行参数统一在此解析（配置文件路径、更新重启的 --wait-for-pid、开机自启维护开关）
                StartupArgs.Parse(args);

                // --install-autostart / --uninstall-autostart：只做开机自启的注册/注销然后退出，不启动 UI。
                // 供 scripts/install.ps1 调用，使计划任务的细节只保留 AutoStartManager 一份实现。
                if (StartupArgs.AutoStart != StartupArgs.AutoStartCommand.None)
                {
                    Environment.ExitCode = RunAutoStartMaintenance(StartupArgs.AutoStart);
                    return;
                }

                Logger.LogInfo("程序启动, 版本: " + App.AppVersionString);

                // 自动更新重启：必须先等旧进程完全退出，再创建窗口/热键/钩子/颜色矩阵，
                // 否则新旧实例会抢同一批全局资源（热键注册失败、双托盘图标、护眼矩阵被旧进程 OnExit 抹掉）
                if (StartupArgs.WaitForPid is int pid)
                    UpdateInstaller.WaitForPreviousInstance(pid);

                // 清理上次更新遗留的 .old 备份与下载临时文件（幂等、静默）
                UpdateInstaller.CleanupLeftovers();

                // 单实例：开机自启起了一个、用户又手动双击一个的情况很常见，两个实例会互抢全局资源
                // （RegisterHotKey 冲突导致热键“失灵”、低级键盘钩子重复安装、双托盘图标）。
                // 拿不到就通知已有实例弹出面板后静默退出。
                //
                // 等待时长分两档：自动更新重启时旧进程可能仍在退出的最后阶段（前面的
                // WaitForPreviousInstance 只等到进程对象消失），多给一点时间；普通重复启动则要尽快退出，
                // 否则用户双击后会对着一个「什么都没发生」的几秒空窗期发呆。
                var mutexWait = StartupArgs.WaitForPid is null
                    ? TimeSpan.FromMilliseconds(500)
                    : TimeSpan.FromSeconds(5);

                if (!SingleInstance.TryAcquire(mutexWait))
                {
                    // 只有「用户重复启动」才应该唤起已有实例。
                    // 自动更新重启（--wait-for-pid）走到这里意味着旧进程超时仍未退出，
                    // 此时广播就成了「让正在被更新掉的旧实例弹出命令面板」——更新中断，动作语义也完全不对，
                    // 该场景应静默退出，把机会留给下一次更新检查。
                    if (StartupArgs.WaitForPid is null)
                        SingleInstance.NotifyExistingInstance();
                    else
                        Logger.LogWarning("更新重启时旧进程仍持有单实例互斥量，本次放弃启动（不唤起旧实例）");
                    return;
                }

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
                // 幂等：没拿到所有权时也可安全调用
                SingleInstance.Release();
                Logger.LogInfo("程序结束");
            }
        }

        /// <summary>
        /// 执行开机自启的一次性维护动作，返回进程退出码（0 成功 / 1 失败），供安装脚本判断结果。
        /// 这条路径不创建 <see cref="App"/>，因此不占用热键、钩子、托盘等全局资源，
        /// 可以在程序正常运行期间被安装脚本安全地调用。
        /// </summary>
        private static int RunAutoStartMaintenance(StartupArgs.AutoStartCommand command)
        {
            bool install = command == StartupArgs.AutoStartCommand.Install;
            Logger.LogInfo(install ? "命令行请求：注册开机自启" : "命令行请求：注销开机自启");

            string error;
            bool ok = install ? AutoStartManager.Enable(out error) : AutoStartManager.Disable(out error);

            if (ok)
                return 0;

            Logger.LogWarning($"开机自启维护失败：{error}");
            return 1;
        }
    }
}

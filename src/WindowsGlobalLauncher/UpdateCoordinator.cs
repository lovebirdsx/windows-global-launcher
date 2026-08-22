using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CommandLauncher
{
    /// <summary>
    /// 更新流程的编排层：把「检查（<see cref="UpdateChecker"/>）」与「提示界面（<see cref="UpdateWindow"/>）」串起来，
    /// 并区分两种入口的策略差异——
    /// 启动时的自动检查要安静（节流、尊重「跳过此版本」、失败不打扰），
    /// 用户手动触发的检查要有回应（忽略节流与跳过标记，没有更新也要明确告知）。
    /// </summary>
    public static class UpdateCoordinator
    {
        /// <summary>启动后延迟多久再检查：避开启动高峰（OCR 引擎下载、配置加载、热键注册）与开机时的网络未就绪。</summary>
        private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(30);

        /// <summary>0 = 空闲，1 = 手动检查进行中（防止用户连点托盘菜单发起多次请求）。</summary>
        private static int _manualChecking;

        /// <summary>
        /// 启动后的自动检查（fire-and-forget 调用）：每天最多一次，发现更高版本且未被跳过时弹出提示窗。
        /// 任何失败都只记日志，绝不弹窗打扰——用户没主动问，就不该被网络问题烦到。
        /// </summary>
        public static async Task RunStartupCheckAsync()
        {
            if (!UpdateChecker.ShouldAutoCheck())
                return;

            await Task.Delay(StartupCheckDelay).ConfigureAwait(false);

            var result = await UpdateChecker.FetchLatestAsync().ConfigureAwait(false);

            if (result.Info == null)
            {
                // 被限流时也记下「今天已经试过」，避免每次启动都去撞同一堵墙；
                // 普通网络失败则不写时间戳，下次启动还能再试一次。
                if (result.RateLimited)
                    MarkCheckedOnUiThread();
                return;
            }

            MarkCheckedOnUiThread();

            var info = result.Info;
            if (!UpdateChecker.IsNewerThanCurrent(info))
                return;

            if (UpdateChecker.IsSkipped(info))
            {
                Logger.LogInfo($"发现新版本 {info.TagName}，但用户已选择跳过该版本，不再提示");
                return;
            }

            Logger.LogInfo($"发现新版本 {info.TagName}（当前 {App.AppVersionString}），弹出更新提示");
            ShowUpdateWindowOnUiThread(info);
        }

        /// <summary>
        /// 用户手动触发的检查（托盘菜单 / 命令面板 "update"）：忽略每日节流与「跳过此版本」，
        /// 并且无论结果如何都给出明确反馈。
        /// </summary>
        public static async Task RunManualCheckAsync()
        {
            if (Interlocked.CompareExchange(ref _manualChecking, 1, 0) != 0)
            {
                Logger.LogInfo("已有检查更新正在进行，忽略本次手动触发");
                return;
            }

            try
            {
                Logger.LogInfo("手动检查更新");
                var result = await UpdateChecker.FetchLatestAsync().ConfigureAwait(false);

                if (result.Error != null)
                {
                    ShowMessageOnUiThread($"检查更新失败：{result.Error}", MessageBoxImage.Warning);
                    return;
                }

                MarkCheckedOnUiThread();

                var info = result.Info;
                if (info == null || !UpdateChecker.IsNewerThanCurrent(info))
                {
                    ShowMessageOnUiThread($"当前已是最新版本（v{App.AppVersionString}）", MessageBoxImage.Information);
                    return;
                }

                Logger.LogInfo($"手动检查发现新版本 {info.TagName}（当前 {App.AppVersionString}）");
                ShowUpdateWindowOnUiThread(info);
            }
            catch (Exception ex)
            {
                Logger.LogError("手动检查更新异常", ex);
                ShowMessageOnUiThread($"检查更新失败：{ex.Message}", MessageBoxImage.Warning);
            }
            finally
            {
                Interlocked.Exchange(ref _manualChecking, 0);
            }
        }

        /// <summary>
        /// 切回 UI 线程记录检查时间。
        /// AppState 是单例且以整文件覆写方式持久化，后台线程直接写有与 UI 线程的其它写入撞车的风险，
        /// 故所有 AppState 写入统一收敛到 UI 线程。
        /// </summary>
        private static void MarkCheckedOnUiThread()
        {
            var app = Application.Current;
            if (app == null)
            {
                // Application 为 null 只可能出现在应用尚未启动或已退出之后，此时不存在与 UI 线程的并发写，
                // 直接写反而能保住这次记录（丢了它只会导致下次启动多检查一次，无害）
                UpdateChecker.MarkChecked();
                return;
            }

            app.Dispatcher.Invoke(UpdateChecker.MarkChecked);
        }

        /// <summary>切回 UI 线程弹出更新窗口（检查是在后台线程完成的）。</summary>
        private static void ShowUpdateWindowOnUiThread(UpdateInfo info)
        {
            var app = Application.Current;
            if (app == null)
                return;

            app.Dispatcher.Invoke(() =>
            {
                try
                {
                    UpdateWindow.ShowFor(info);
                }
                catch (Exception ex)
                {
                    Logger.LogError("弹出更新提示窗失败", ex);
                }
            });
        }

        private static void ShowMessageOnUiThread(string message, MessageBoxImage icon)
        {
            var app = Application.Current;
            if (app == null)
                return;

            app.Dispatcher.Invoke(() => MessageBox.Show(message, "检查更新", MessageBoxButton.OK, icon));
        }
    }
}

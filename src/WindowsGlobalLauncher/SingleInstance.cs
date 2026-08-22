using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CommandLauncher
{
    /// <summary>
    /// 单实例互斥：保证同一登录会话内只有一个本程序实例在运行。
    /// 背景：程序即将支持「安装到用户目录」和「开机自动启动」，重复启动第二个实例几乎必然发生
    /// （开机自启起了一个，用户又手动双击一个）。两个实例会互抢全局资源：<c>RegisterHotKey</c> 冲突
    /// （第二个注册失败，用户以为热键坏了）、低级键盘钩子重复安装、双托盘图标、退出时互相抹掉护眼颜色矩阵。
    /// 因此第二个实例检测到已有实例后，通过广播消息通知已有实例弹出命令面板，然后自己静默退出。
    /// 全静态实现，与项目里 <see cref="Logger"/>/<see cref="EyeCareManager"/> 的风格一致。
    /// </summary>
    public static class SingleInstance
    {
        // 互斥量名用固定 GUID 后缀避免与其它程序撞名。
        // 用 Local\ 而非 Global\：本程序的实例冲突只发生在同一登录会话内，
        // Local\ 权限要求更低、更不容易在多用户机器上互相干扰。
        private const string MutexName = @"Local\WindowsGlobalLauncher-Mutex-{5D2B6C8F-1E4A-4A7B-9C3D-8F0E2A6B1D4C}";

        // RegisterWindowMessage 保证同一字符串在全系统返回同一个消息 ID（0xC000~0xFFFF 区间），
        // 因此两个实例各自调用都能得到一致的 ID，无需预分配常量。
        private const string MessageName = "WindowsGlobalLauncher.ShowLauncher";

        // 广播到所有顶层窗口的标准句柄（PostMessage 的 hWnd 参数）
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // 互斥量及其所有权标记。所有权必须显式记录：未持有所有权时调用 ReleaseMutex 会抛 ApplicationException。
        private static Mutex? _mutex;
        private static bool _hasOwnership;

        // 消息窗口与回调必须用静态字段强引用，防止被 GC 回收导致监听失效（同项目里键盘钩子的注意事项）。
        private static ListenerWindow? _window;
        private static Action? _onActivateRequested;

        // 唤起消息 ID 懒加载（RegisterWindowMessage 全系统幂等，首次调用后缓存）
        private static uint _messageId;
        private static bool _messageIdInitialized;

        /// <summary>
        /// 尝试成为唯一实例。拿到所有权返回 true；已有实例在运行返回 false。
        /// <paramref name="wait"/> 是等待已有实例退出的最长时间（自动更新重启场景下旧进程可能正在退出）。
        /// 任何异常都记 LogError 并返回 true（宁可放行启动，也不要因为互斥量本身出问题导致程序完全起不来）。
        /// </summary>
        public static bool TryAcquire(TimeSpan wait)
        {
            try
            {
                _mutex = new Mutex(false, MutexName, out bool createdNew);

                // 不要只依赖 createdNew：即使 mutex 已存在，旧实例可能正在退出，
                // 用 WaitOne(wait) 竞争所有权才能支持「等旧实例退出」。
                try
                {
                    _hasOwnership = _mutex.WaitOne(wait);
                }
                catch (AbandonedMutexException)
                {
                    // 经典坑：上一个进程被强杀（scripts/publish.ps1 每次发布都 Stop-Process -Force）时
                    // 互斥量会处于 abandoned 状态，WaitOne 抛出 AbandonedMutexException，
                    // 但此时所有权实际上已转移给我们，应视为获取成功，而不是失败。
                    _hasOwnership = true;
                }

                if (_hasOwnership)
                    Logger.LogInfo("已获得单实例所有权");
                else
                    Logger.LogWarning("检测到已有实例正在运行，将通知其弹出命令面板后退出");

                return _hasOwnership;
            }
            catch (Exception ex)
            {
                // 降级策略：互斥量创建/等待失败时放行启动，绝不因单实例机制拖垮正常启动。
                Logger.LogError("单实例互斥量初始化失败，降级为允许启动", ex);
                _hasOwnership = false;
                return true;
            }
        }

        /// <summary>
        /// 通知已在运行的实例弹出命令面板。由拿不到互斥量的那个实例在退出前调用。
        /// 成功投递返回 true（不等待对方响应——广播是异步投递，对方卡住也不会把自己拖死）。
        /// 注意：PostMessage 到 HWND_BROADCAST 即使没有任何窗口收到消息也返回 true，
        /// 所以这里的「成功」仅代表投递动作成功，不代表对方确实收到。
        /// </summary>
        public static bool NotifyExistingInstance()
        {
            try
            {
                bool result = PostMessage(HWND_BROADCAST, GetMessageId(), IntPtr.Zero, IntPtr.Zero);
                if (!result)
                {
                    Logger.LogWarning($"向已有实例发送唤起消息失败: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                    return false;
                }

                Logger.LogInfo("已通知已有实例弹出命令面板");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"向已有实例发送唤起消息异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 在 UI 线程调用：创建隐藏消息窗口，开始接收其它实例的唤起请求。
        /// <paramref name="onActivateRequested"/> 在 UI 线程上被调用。重复调用应幂等（只装一次）。
        /// </summary>
        public static void StartListening(Action onActivateRequested)
        {
            if (_window != null)
                return; // 幂等：已经在监听，直接返回

            _onActivateRequested = onActivateRequested;

            // 隐藏消息窗口（同 HotKeyListener 的 NativeWindow 做法）：
            // HwndSource 不支持 message-only parent，故用 WinForms NativeWindow 而非 WPF HwndSource。
            _window = new ListenerWindow();
            _window.CreateHandle(new CreateParams());

            Logger.LogInfo("单实例唤起消息监听已启动");
        }

        /// <summary>
        /// 释放互斥量与消息窗口。幂等，绝不抛异常。由 Program.Main 的 finally 调用。
        /// </summary>
        public static void Release()
        {
            try
            {
                _window?.DestroyHandle();
            }
            catch (Exception ex)
            {
                Logger.LogError("销毁单实例消息窗口失败", ex);
            }
            finally
            {
                _window = null;
            }

            try
            {
                if (_mutex != null)
                {
                    // 只有持有所有权时才 ReleaseMutex，否则会抛 ApplicationException
                    if (_hasOwnership)
                    {
                        _mutex.ReleaseMutex();
                        _hasOwnership = false;
                    }
                    _mutex.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("释放单实例互斥量失败", ex);
            }
            finally
            {
                _mutex = null;
            }

            Logger.LogInfo("单实例资源已释放");
        }

        /// <summary>懒加载并缓存唤起消息 ID。</summary>
        private static uint GetMessageId()
        {
            if (!_messageIdInitialized)
            {
                _messageId = RegisterWindowMessage(MessageName);
                _messageIdInitialized = true;
                if (_messageId == 0)
                    Logger.LogError("注册唤起消息失败", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            return _messageId;
        }

        /// <summary>
        /// 接收唤起广播消息的隐藏窗口。回调包在 try/catch 里，绝不让异常穿出 WndProc。
        /// </summary>
        private class ListenerWindow : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                // 必须先排除 0：RegisterWindowMessage 失败时返回 0，而 0 就是 WM_NULL——
                // 系统和本程序（ForegroundActivator.IsWindowHung）都用 WM_NULL 探测窗口是否挂起，
                // 不加这个判断会让命令面板被无关的探测消息随机弹出来。
                uint messageId = GetMessageId();
                if (messageId != 0 && m.Msg == (int)messageId)
                {
                    try
                    {
                        // 本程序恒以管理员（高完整性级别）运行，两个实例完整性级别相同，
                        // 广播消息不会被 UIPI 过滤，因此无需 ChangeWindowMessageFilterEx。
                        Logger.LogInfo("收到其它实例的唤起请求");
                        _onActivateRequested?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("处理唤起请求异常", ex);
                    }
                }
                base.WndProc(ref m);
            }
        }
    }
}

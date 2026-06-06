using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommandLauncher
{
    /// <summary>
    /// 封装 Win32 调用：枚举可切换窗口、提取窗口图标、激活窗口。
    /// 用于 Alt+Tab 窗口切换器。
    /// </summary>
    public static class WindowEnumerator
    {
        #region Win32 P/Invoke

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
            IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
        private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_EX_APPWINDOW = 0x00040000;
        private const uint GW_OWNER = 4;

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        private const uint WM_GETICON = 0x007F;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const int ICON_SMALL2 = 2;
        private const int GCLP_HICON = -14;
        private const int GCLP_HICONSM = -34;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint WM_CLOSE = 0x0010;

        private const int DWMWA_CLOAKED = 14;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        private const int SW_MAXIMIZE      = 3;
        private const uint SWP_NOZORDER    = 0x0004;
        private const uint SWP_NOACTIVATE  = 0x0010;

        #endregion

        // 进程 exe 提取出的图标较昂贵，按 exe 路径缓存
        private static readonly Dictionary<string, ImageSource?> _exeIconCache = new();

        /// <summary>
        /// 枚举所有可参与 Alt+Tab 的顶层窗口，顺序为 Z 序（≈MRU，首项为当前前台窗口）。
        /// </summary>
        public static List<WindowInfo> EnumerateWindows(IntPtr excludeHwnd,
            IReadOnlySet<IntPtr>? flashingWindows = null)
        {
            var result = new List<WindowInfo>();

            EnumWindows((hwnd, _) =>
            {
                if (!IsAltTabWindow(hwnd, excludeHwnd))
                    return true; // 继续枚举

                string title = GetTitle(hwnd);
                if (string.IsNullOrWhiteSpace(title))
                    return true;

                GetWindowThreadProcessId(hwnd, out uint pid);

                result.Add(new WindowInfo
                {
                    Hwnd = hwnd,
                    Title = title,
                    Icon = GetWindowIcon(hwnd, pid),
                    ProcessName = GetProcessName(pid),
                    HasNotification = flashingWindows?.Contains(hwnd) == true
                });
                return true;
            }, IntPtr.Zero);

            return result;
        }

        /// <summary>
        /// 激活（切换到）指定窗口。
        /// 通过 AttachThreadInput 把当前线程附加到前台线程的输入队列，绕过 Windows 前台锁定，
        /// 解决键盘触发切换时仅任务栏闪烁、窗口不前置的问题。
        /// </summary>
        public static void Activate(IntPtr hwnd)
        {
            try
            {
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SW_RESTORE);

                IntPtr foreground = GetForegroundWindow();
                if (foreground == hwnd)
                    return;

                uint thisThread = GetCurrentThreadId();
                uint foreThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
                uint targetThread = GetWindowThreadProcessId(hwnd, out _);

                bool attachedFore = false;
                bool attachedTarget = false;
                if (foreThread != 0 && foreThread != thisThread)
                    attachedFore = AttachThreadInput(thisThread, foreThread, true);
                if (targetThread != 0 && targetThread != thisThread && targetThread != foreThread)
                    attachedTarget = AttachThreadInput(thisThread, targetThread, true);

                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                ShowWindow(hwnd, SW_SHOW);

                if (attachedFore)
                    AttachThreadInput(thisThread, foreThread, false);
                if (attachedTarget)
                    AttachThreadInput(thisThread, targetThread, false);

                // 兜底：仍未到前台时，使用 Alt+Tab 内部 API
                if (GetForegroundWindow() != hwnd)
                    SwitchToThisWindow(hwnd, true);
            }
            catch (Exception ex)
            {
                Logger.LogError($"激活窗口失败: {hwnd}", ex);
            }
        }

        /// <summary>
        /// 优雅关闭指定窗口：发送 WM_CLOSE（等同点窗口右上角的 X），
        /// 给程序保存数据的机会，不强杀进程。
        /// 用 SendMessageTimeout + SMTO_ABORTIFHUNG，避免被卡死窗口阻塞调用线程。
        /// </summary>
        public static void CloseWindow(IntPtr hwnd)
        {
            try
            {
                SendMessageTimeout(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 1000, out _);
                Logger.LogInfo($"已请求关闭窗口: {hwnd}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"关闭窗口失败: {hwnd}", ex);
            }
        }

        /// <summary>
        /// 将窗口移到相邻显示器（按物理左右位置排列）。
        /// direction = -1 左移，+1 右移；已在最左/最右时静默忽略。
        /// 若窗口最大化，先还原再移动，然后在目标显示器上重新最大化。
        /// </summary>
        public static void MoveToAdjacentMonitor(IntPtr hwnd, int direction)
        {
            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens
                    .OrderBy(s => s.Bounds.Left)
                    .ToArray();

                if (screens.Length < 2)
                    return;

                var current = System.Windows.Forms.Screen.FromHandle(hwnd);
                int idx = Array.FindIndex(screens, s => s.DeviceName == current.DeviceName);
                if (idx < 0)
                    return;

                int targetIdx = idx + direction;
                if (targetIdx < 0 || targetIdx >= screens.Length)
                    return; // 已在边缘，不回绕

                var target = screens[targetIdx];
                bool wasMaximized = IsZoomed(hwnd);
                if (wasMaximized)
                    ShowWindow(hwnd, SW_RESTORE);

                GetWindowRect(hwnd, out RECT rect);
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;

                var wa = target.WorkingArea;
                int newX = Math.Max(wa.Left, Math.Min(wa.Left + (wa.Width - w) / 2, wa.Right - w));
                int newY = Math.Max(wa.Top,  Math.Min(wa.Top  + (wa.Height - h) / 2, wa.Bottom - h));

                SetWindowPos(hwnd, IntPtr.Zero, newX, newY, w, h, SWP_NOZORDER | SWP_NOACTIVATE);

                if (wasMaximized)
                    ShowWindow(hwnd, SW_MAXIMIZE);

                Logger.LogInfo($"窗口 {hwnd} 已移至{(direction < 0 ? "左" : "右")}侧显示器: {target.DeviceName}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"移动窗口到相邻显示器失败: {hwnd}", ex);
            }
        }

        private static bool IsAltTabWindow(IntPtr hwnd, IntPtr excludeHwnd)
        {
            if (hwnd == excludeHwnd)
                return false;
            if (!IsWindowVisible(hwnd))
                return false;
            if (GetWindowTextLength(hwnd) == 0)
                return false;

            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                return false;

            bool isAppWindow = (exStyle & WS_EX_APPWINDOW) != 0;
            IntPtr owner = GetWindow(hwnd, GW_OWNER);
            if (owner != IntPtr.Zero && !isAppWindow)
                return false;

            if (IsCloaked(hwnd))
                return false;

            return true;
        }

        private static bool IsCloaked(IntPtr hwnd)
        {
            try
            {
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
                    return cloaked != 0;
            }
            catch
            {
                // dwmapi 不可用时忽略
            }
            return false;
        }

        private static string GetTitle(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            if (len == 0)
                return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static ImageSource? GetWindowIcon(IntPtr hwnd, uint pid)
        {
            // 1. WM_GETICON（窗口级图标，句柄归窗口所有，不可销毁）
            IntPtr hIcon = TryGetIcon(hwnd, ICON_SMALL2);
            if (hIcon == IntPtr.Zero) hIcon = TryGetIcon(hwnd, ICON_BIG);
            if (hIcon == IntPtr.Zero) hIcon = TryGetIcon(hwnd, ICON_SMALL);

            // 2. 类图标（句柄归窗口类所有，不可销毁）
            if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hwnd, GCLP_HICONSM);
            if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hwnd, GCLP_HICON);

            if (hIcon != IntPtr.Zero)
            {
                var img = IconHandleToImageSource(hIcon);
                if (img != null)
                    return img;
            }

            // 3. 回退：从进程 exe 提取关联图标（昂贵，按 exe 路径缓存）
            return GetIconFromProcess(pid);
        }

        private static IntPtr TryGetIcon(IntPtr hwnd, int iconType)
        {
            SendMessageTimeout(hwnd, WM_GETICON, new IntPtr(iconType), IntPtr.Zero,
                SMTO_ABORTIFHUNG, 200, out IntPtr result);
            return result;
        }

        private static ImageSource? GetIconFromProcess(uint pid)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                string? exePath = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                    return null;

                if (_exeIconCache.TryGetValue(exePath, out var cached))
                    return cached;

                ImageSource? img = null;
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                {
                    if (icon != null)
                        img = IconHandleToImageSource(icon.Handle);
                }

                _exeIconCache[exePath] = img;
                return img;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"提取进程图标失败 (pid={pid}): {ex.Message}");
                return null;
            }
        }

        private static ImageSource? IconHandleToImageSource(IntPtr hIcon)
        {
            try
            {
                var img = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze(); // 冻结，跨线程安全且利于性能
                return img;
            }
            catch
            {
                return null;
            }
        }

        private static string GetProcessName(uint pid)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
            catch
            {
                return "";
            }
        }

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

        private static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, nIndex) : new IntPtr(GetClassLong32(hWnd, nIndex));
    }
}

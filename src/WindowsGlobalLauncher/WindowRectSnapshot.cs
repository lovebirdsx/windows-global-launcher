using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>截图遮罩弹出前的顶层窗口矩形快照（Z 序），用于悬停自动吸附窗口边界。</summary>
    public sealed class WindowRectSnapshot
    {
        private readonly List<System.Drawing.Rectangle> _rectsInZOrder;

        private WindowRectSnapshot(List<System.Drawing.Rectangle> rectsInZOrder) => _rectsInZOrder = rectsInZOrder;

        /// <summary>
        /// 枚举当前「可吸附」的顶层窗口，按 Z 序记录其物理像素矩形。
        /// 过滤条件借鉴 WindowEnumerator.IsAltTabWindow 但更宽松：截图吸附对象不限于 Alt+Tab 窗口，
        /// 故不检查 owner/WS_EX_APPWINDOW；只排除不可见、最小化、无标题、工具窗口、DWM cloaked、
        /// excludeHwnd 以及本进程窗口（避免吸附到启动器自身的隐藏窗口）。
        /// </summary>
        public static WindowRectSnapshot Capture(IntPtr excludeHwnd)
        {
            var rects = new List<System.Drawing.Rectangle>();
            uint selfPid = (uint)Environment.ProcessId;

            EnumWindows((hwnd, _) =>
            {
                if (hwnd == excludeHwnd)
                    return true; // 继续枚举
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                    return true;
                if (GetWindowTextLength(hwnd) == 0)
                    return true;

                long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                    return true;

                if (IsCloaked(hwnd))
                    return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == selfPid)
                    return true;

                if (TryGetFrameBounds(hwnd, out var rect) && rect.Width > 0 && rect.Height > 0)
                    rects.Add(rect);
                return true;
            }, IntPtr.Zero);

            Logger.LogInfo($"窗口矩形快照完成：{rects.Count} 个顶层窗口");
            return new WindowRectSnapshot(rects);
        }

        /// <summary>按 Z 序返回最顶层包含该物理像素点的窗口矩形；无命中返回 null。</summary>
        public System.Drawing.Rectangle? HitTest(System.Drawing.Point physicalPt)
        {
            foreach (var rect in _rectsInZOrder)
            {
                if (rect.Contains(physicalPt))
                    return rect;
            }
            return null;
        }

        /// <summary>优先取 DWM 扩展框架边界（真实可见边界，物理像素），失败回退 GetWindowRect。</summary>
        private static bool TryGetFrameBounds(IntPtr hwnd, out System.Drawing.Rectangle rect)
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0
                || GetWindowRect(hwnd, out frame))
            {
                rect = System.Drawing.Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
                return true;
            }
            rect = default;
            return false;
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

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

        #region Win32 P/Invoke（按仓库惯例内联声明，允许与其他文件重复）

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        #endregion
    }
}

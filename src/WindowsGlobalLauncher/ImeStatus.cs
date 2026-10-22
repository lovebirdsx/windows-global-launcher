using System;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>输入法状态提示的展示内容（None 表示不显示提示）。</summary>
    public enum ImeDisplay
    {
        None,
        Chinese,
        English,
    }

    /// <summary>
    /// 输入法状态判定的纯逻辑部分（不依赖 Win32、可单测）。
    /// 规则对齐 Windows 语言栏：中文 IME 按转换模式显示中/英；
    /// 中文键盘布局（非 IME）显示「英」；非中文 IME（日文/韩文等）不显示。
    /// </summary>
    public static class ImeStatusResolver
    {
        /// <summary>IME_CMODE_NATIVE：转换模式中的「本地语言（中文）」位。</summary>
        public const uint ImeCmodeNative = 0x0001;

        /// <summary>中文（中国大陆、台湾、香港、新加坡等）的主语言 ID。</summary>
        private const ushort LangChinese = 0x04;

        /// <summary>
        /// 语言 ID 的低 10 位是主语言 ID：zh-CN(0x0804)、zh-TW(0x0404)、
        /// zh-HK(0x0C04)、zh-SG(0x1004) 均命中。
        /// </summary>
        public static bool IsChineseLanguageId(ushort languageId)
        {
            return (languageId & 0x3FF) == LangChinese;
        }

        /// <summary>
        /// 根据键盘布局与 IME 转换模式判定应显示的状态。
        /// modeKnown = false 表示中文 IME 的转换模式查询失败，此时不猜（返回 None）。
        /// </summary>
        public static ImeDisplay Resolve(
            ushort languageId, bool isIme, bool hasChineseLayout,
            uint conversionMode, bool modeKnown)
        {
            if (IsChineseLanguageId(languageId))
            {
                if (!isIme)
                {
                    // 中文键盘布局但不是 IME（如「中文(简体) - 美式键盘」）：显示「英」
                    return ImeDisplay.English;
                }

                if (!modeKnown)
                {
                    // 中文 IME 但转换模式查询失败：宁可不显示，也不猜
                    return ImeDisplay.None;
                }

                return (conversionMode & ImeCmodeNative) != 0
                    ? ImeDisplay.Chinese
                    : ImeDisplay.English;
            }

            if (isIme)
            {
                // 日文/韩文等非中文 IME：不显示
                return ImeDisplay.None;
            }

            // 非中文键盘布局：系统规则是装了中文布局时英文键盘布局也显示「英」
            return hasChineseLayout ? ImeDisplay.English : ImeDisplay.None;
        }

        /// <summary>状态转提示文本（"中" / "英" / 空串）。</summary>
        public static string ToText(ImeDisplay display)
        {
            return display switch
            {
                ImeDisplay.Chinese => "中",
                ImeDisplay.English => "英",
                _ => "",
            };
        }
    }

    /// <summary>
    /// 一次输入法查询的结果。Trustworthy = false 表示查询失败/不可信，
    /// 调用方应静默隐藏提示而不是把 Display 当作有效状态。
    /// </summary>
    public readonly record struct ImeQueryResult(ImeDisplay Display, bool Trustworthy);

    /// <summary>
    /// 输入法状态的 Win32 查询（在后台线程调用）。
    /// 只读、不弹窗、绝不抛异常：任何失败都返回 (None, false)。
    /// </summary>
    internal static class ImeStatusReader
    {
        private const uint WM_IME_CONTROL = 0x0283;
        private const uint IMC_GETCONVERSIONMODE = 0x0001;
        private const uint SMTO_BLOCK = 0x0001;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint ImeQueryTimeoutMs = 50;

        private const long LayoutCacheDurationMs = 30_000;

        private static readonly object LayoutCacheLock = new object();
        private static bool _hasChineseLayout;
        private static bool _layoutCacheValid;
        private static long _layoutCacheTick;

        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int dwFlags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("imm32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImmIsIME(IntPtr hkl);

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);

        /// <summary>
        /// 查询前台窗口的输入法状态（兼容旧调用：焦点按前台窗口本身处理）。
        /// 后台线程调用，任何异常都吞掉并返回不可信结果。
        /// </summary>
        internal static ImeQueryResult Query(IntPtr foreground)
        {
            return Query(foreground, IntPtr.Zero, 0);
        }

        /// <summary>
        /// 按真实焦点窗口/线程查询输入法状态（后台线程调用）。
        /// 顶层窗口只作前台一致性校验与必要回退；焦点已移出前台时返回不可信，
        /// 绝不把别的窗口的键盘布局当成当前输入法。
        /// </summary>
        internal static ImeQueryResult Query(IntPtr foreground, IntPtr focusHwnd, uint focusThreadId)
        {
            try
            {
                if (foreground == IntPtr.Zero)
                {
                    // 无前台窗口（锁屏、桌面切换等）
                    return new ImeQueryResult(ImeDisplay.None, false);
                }

                if (focusHwnd == IntPtr.Zero)
                {
                    // 拿不到真实焦点时的回退：按前台窗口自身查询（旧行为）
                    focusHwnd = foreground;
                    focusThreadId = 0;
                }
                else
                {
                    // 顶层窗口一致性：焦点窗口必须属于当前前台窗口。
                    // 焦点已落到别的顶层窗口说明调用方拿到的身份已过期，不猜。
                    IntPtr root = GetAncestor(focusHwnd, GA_ROOT);
                    if (root == IntPtr.Zero || root != foreground)
                        return new ImeQueryResult(ImeDisplay.None, false);
                }

                uint threadId = focusThreadId != 0
                    ? focusThreadId
                    : GetWindowThreadProcessId(focusHwnd, out _);
                if (threadId == 0)
                {
                    return new ImeQueryResult(ImeDisplay.None, false);
                }

                IntPtr hkl = GetKeyboardLayout(threadId);
                if (hkl == IntPtr.Zero)
                {
                    return new ImeQueryResult(ImeDisplay.None, false);
                }

                // 语言 ID 位于 HKL 的低 16 位
                ushort languageId = (ushort)((long)hkl & 0xFFFF);
                bool isIme = ImmIsIME(hkl);
                bool hasChineseLayout = HasChineseLayoutCached();

                if (ImeStatusResolver.IsChineseLanguageId(languageId) && isIme)
                {
                    // 中文 IME：向真实焦点窗口的默认 IME 窗口查询转换模式，
                    // 取不到时才回退前台窗口（顶层窗口仅作必要兜底）。
                    IntPtr imeWnd = ImmGetDefaultIMEWnd(focusHwnd);
                    if (imeWnd == IntPtr.Zero && focusHwnd != foreground)
                        imeWnd = ImmGetDefaultIMEWnd(foreground);

                    if (imeWnd == IntPtr.Zero)
                    {
                        return new ImeQueryResult(ImeDisplay.None, false);
                    }

                    IntPtr sendResult = SendMessageTimeout(
                        imeWnd,
                        WM_IME_CONTROL,
                        new IntPtr(IMC_GETCONVERSIONMODE),
                        IntPtr.Zero,
                        SMTO_ABORTIFHUNG | SMTO_BLOCK,
                        ImeQueryTimeoutMs,
                        out IntPtr conversionResult);

                    if (sendResult == IntPtr.Zero)
                    {
                        // 返回 0 = 超时或失败，此时 conversionResult 不可信；
                        // 合法的英文模式 0 只会出现在返回值非 0 的成功分支。
                        return new ImeQueryResult(ImeDisplay.None, false);
                    }

                    uint conversionMode = (uint)conversionResult.ToInt64();
                    return new ImeQueryResult(
                        ImeStatusResolver.Resolve(languageId, isIme, hasChineseLayout, conversionMode, true),
                        true);
                }

                // 中文键盘布局（非 IME）或非中文布局：无需查询转换模式
                return new ImeQueryResult(
                    ImeStatusResolver.Resolve(languageId, isIme, hasChineseLayout, 0, true),
                    true);
            }
            catch
            {
                // 查询路径绝不向调用方抛异常（调用方是后台线程，异常会静默丢失）
                return new ImeQueryResult(ImeDisplay.None, false);
            }
        }

        /// <summary>
        /// 取当前前台窗口的真实焦点窗口与线程（纯 Win32，快速、可在探测线程调用）。
        /// 拿不到焦点时回退为前台窗口本身；失败返回 false（不猜）。
        /// </summary>
        internal static bool TryGetFocusWindow(
            IntPtr foreground, out IntPtr focusHwnd, out uint focusThreadId, out uint processId)
        {
            focusHwnd = IntPtr.Zero;
            focusThreadId = 0;
            processId = 0;

            try
            {
                if (foreground == IntPtr.Zero)
                    return false;

                uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
                if (foregroundThread == 0)
                    return false;

                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (!GetGUIThreadInfo(foregroundThread, ref info))
                    return false;

                IntPtr focus = info.hwndFocus != IntPtr.Zero ? info.hwndFocus : foreground;
                uint thread = GetWindowThreadProcessId(focus, out uint pid);
                if (thread == 0)
                    return false;

                focusHwnd = focus;
                focusThreadId = thread;
                processId = pid;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 系统是否安装了任一中文键盘布局（结果缓存 30 秒）。
        /// 查询失败返回 false 且不写入缓存，下次重新查询。
        /// </summary>
        private static bool HasChineseLayoutCached()
        {
            lock (LayoutCacheLock)
            {
                if (_layoutCacheValid &&
                    Environment.TickCount64 - _layoutCacheTick < LayoutCacheDurationMs)
                {
                    return _hasChineseLayout;
                }

                // 先取数量再取列表；两次调用之间布局可能变化，按第二次实际返回数量处理
                int count = GetKeyboardLayoutList(0, null);
                if (count <= 0)
                {
                    return false;
                }

                var layouts = new IntPtr[count];
                int actual = GetKeyboardLayoutList(count, layouts);
                if (actual <= 0)
                {
                    return false;
                }

                bool found = false;
                for (int i = 0; i < actual && i < layouts.Length; i++)
                {
                    ushort languageId = (ushort)((long)layouts[i] & 0xFFFF);
                    if (ImeStatusResolver.IsChineseLanguageId(languageId))
                    {
                        found = true;
                        break;
                    }
                }

                _hasChineseLayout = found;
                _layoutCacheTick = Environment.TickCount64;
                _layoutCacheValid = true;
                return found;
            }
        }
    }
}

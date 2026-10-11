using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CommandLauncher
{
    /// <summary>运行时按键映射方案（由 ConfigKeyMappingProfile 校验构建，钩子热路径只读）。</summary>
    public sealed class KeyMappingProfileRuntime
    {
        public required string Name { get; init; }

        /// <summary>进程白名单（不区分大小写）；为空表示全局生效（本程序自身窗口除外）。</summary>
        public required IReadOnlyList<string> Processes { get; init; }

        /// <summary>源 vk → 目标 vk。</summary>
        public required IReadOnlyDictionary<int, int> Mappings { get; init; }
    }

    /// <summary>按键映射的校验构建与匹配（纯静态、可单测，不碰 UI 与钩子）。</summary>
    public static class KeyMapping
    {
        /// <summary>
        /// 从配置方案构建运行时方案：From/To 必须是无修饰键的单个主键（修饰键本就会原样透传，
        /// 写进配置无意义）、From != To、同方案内 From 不得重复（保留首条）。
        /// 非法条目跳过并记 WARN；全部无效时返回 null（该方案不生效）。
        /// </summary>
        public static KeyMappingProfileRuntime? BuildProfile(ConfigKeyMappingProfile config)
        {
            var mappings = new Dictionary<int, int>();
            foreach (var item in config.Mappings)
            {
                if (!TryParseMainKey(item.From, out int from))
                {
                    Logger.LogWarning($"按键映射方案「{config.Name}」源键无效（须为单个主键、不带修饰键），已跳过: {item.From}");
                    continue;
                }

                if (!TryParseMainKey(item.To, out int to))
                {
                    Logger.LogWarning($"按键映射方案「{config.Name}」目标键无效（须为单个主键、不带修饰键），已跳过: {item.From} → {item.To}");
                    continue;
                }

                if (from == to)
                {
                    Logger.LogWarning($"按键映射方案「{config.Name}」源键与目标键相同，已跳过: {item.From}");
                    continue;
                }

                if (mappings.ContainsKey(from))
                {
                    Logger.LogWarning($"按键映射方案「{config.Name}」源键重复，已保留首条: {item.From}");
                    continue;
                }

                mappings[from] = to;
            }

            if (mappings.Count == 0)
                return null;

            return new KeyMappingProfileRuntime
            {
                Name = config.Name,
                Processes = config.Processes.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList(),
                Mappings = mappings
            };
        }

        /// <summary>
        /// 在已装配方案中查源键的映射目标（钩子热路径，必须轻量：仅字符串比较 + 字典查找）。
        /// 按配置顺序取第一个命中的方案（顺序即优先级）；processName 为空
        /// （无前台 / 前台为本程序自身 / 解析失败）时一律不命中，直接透传。
        /// </summary>
        public static bool TryResolve(IReadOnlyList<KeyMappingProfileRuntime> profiles, string? processName, int sourceVk, out int targetVk)
        {
            targetVk = 0;
            if (string.IsNullOrEmpty(processName))
                return false;

            foreach (var profile in profiles)
            {
                if (!IsProcessMatch(profile, processName))
                    continue;

                if (profile.Mappings.TryGetValue(sourceVk, out int target))
                {
                    targetVk = target;
                    return true;
                }
            }

            return false;
        }

        /// <summary>空白名单 = 全局生效；否则进程名忽略大小写比较。</summary>
        private static bool IsProcessMatch(KeyMappingProfileRuntime profile, string processName)
        {
            if (profile.Processes.Count == 0)
                return true;

            foreach (var name in profile.Processes)
            {
                if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>解析单个主键名：必须可解析且不带任何修饰键。</summary>
        private static bool TryParseMainKey(string? key, out int vk)
        {
            vk = 0;
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (!HotKeyParser.TryParse(key, out int parsed, out bool ctrl, out bool alt, out bool shift, out bool win))
                return false;

            if (ctrl || alt || shift || win)
                return false;

            vk = parsed;
            return true;
        }
    }

    /// <summary>
    /// 前台窗口进程名缓存：仅前台窗口句柄变化时才做跨进程查询（OpenProcess + QueryFullProcessImageName），
    /// 钩子回调热路径只做 GetForegroundWindow + 句柄比对，保持轻量（LowLevelHooksTimeout）。
    /// </summary>
    public static class ForegroundProcessCache
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static readonly int OwnPid = Environment.ProcessId;
        private static IntPtr _lastHwnd;
        private static string _lastProcessName = "";

        /// <summary>
        /// 前台窗口的进程名（含扩展名，如 StarCraft.exe）。返回空串 = 不映射：
        /// 无前台窗口（锁屏等）、前台为本程序自身（保证命令面板/剪贴板窗口正常输入）、
        /// 或进程名解析失败（fail-open 透传，每次前台变化最多记一条 WARN，不逐键刷屏）。
        /// </summary>
        public static string GetForegroundProcessName()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return "";

            if (hwnd == _lastHwnd)
                return _lastProcessName;

            _lastHwnd = hwnd;
            _lastProcessName = ResolveProcessName(hwnd);
            return _lastProcessName;
        }

        private static string ResolveProcessName(IntPtr hwnd)
        {
            try
            {
                _ = GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                    return "";

                if (pid == (uint)OwnPid)
                    return ""; // 本程序自身窗口前台：豁免映射

                IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (process == IntPtr.Zero)
                {
                    Logger.LogWarning($"按键映射：打开前台进程失败（pid {pid}，错误 {Marshal.GetLastWin32Error()}），本次不映射");
                    return "";
                }

                try
                {
                    var buffer = new StringBuilder(1024);
                    uint size = (uint)buffer.Capacity;
                    if (!QueryFullProcessImageName(process, 0, buffer, ref size))
                    {
                        Logger.LogWarning($"按键映射：查询前台进程名失败（pid {pid}，错误 {Marshal.GetLastWin32Error()}），本次不映射");
                        return "";
                    }

                    return Path.GetFileName(buffer.ToString());
                }
                finally
                {
                    CloseHandle(process);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("按键映射：解析前台进程名异常，本次不映射", ex);
                return "";
            }
        }
    }

    /// <summary>按键注入相关纯函数。</summary>
    public static class KeyMappingInjection
    {
        /// <summary>
        /// 是否为需要 KEYEVENTF_EXTENDEDKEY 标志的扩展键（方向键 / Home / End / PgUp / PgDn / Insert / Delete）。
        /// 不加该标志时部分应用（尤其 DirectInput 时代的老游戏）会把注入键识别成小键盘对应键。
        /// </summary>
        public static bool IsExtendedKey(int vk) => vk is
            0x21 or  // Page Up
            0x22 or  // Page Down
            0x23 or  // End
            0x24 or  // Home
            0x25 or  // Left
            0x26 or  // Up
            0x27 or  // Right
            0x28 or  // Down
            0x2D or  // Insert
            0x2E;    // Delete
    }
}

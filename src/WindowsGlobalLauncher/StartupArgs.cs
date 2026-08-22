using System;

namespace CommandLauncher
{
    /// <summary>
    /// 命令行参数解析（进程内唯一入口，由 <see cref="Program.Main"/> 在最开始调用一次）。
    /// <para>
    /// 历史上 <c>MainWindow</c> 直接把 <c>Environment.GetCommandLineArgs()[1]</c> 当配置文件路径；
    /// 自动更新重启需要传 <c>--wait-for-pid &lt;pid&gt;</c>，若不统一解析，该参数会被误当成配置文件路径。
    /// 因此所有命令行参数一律经本类读取，不要再在别处直接读 <c>GetCommandLineArgs</c>。
    /// </para>
    /// </summary>
    public static class StartupArgs
    {
        /// <summary>开机自启相关的一次性维护动作（执行完即退出，不启动 UI）。</summary>
        public enum AutoStartCommand
        {
            /// <summary>无（正常启动）。</summary>
            None,

            /// <summary><c>--install-autostart</c>：注册开机自启计划任务后退出。</summary>
            Install,

            /// <summary><c>--uninstall-autostart</c>：注销开机自启计划任务后退出。</summary>
            Uninstall,
        }

        /// <summary>
        /// <c>--wait-for-pid &lt;pid&gt;</c>：自动更新重启时由旧进程传入。
        /// 新进程必须先等该进程退出，再初始化窗口/热键/钩子/颜色矩阵，避免新旧实例抢占同一批全局资源。
        /// </summary>
        public static int? WaitForPid { get; private set; }

        /// <summary>
        /// <c>--install-autostart</c> / <c>--uninstall-autostart</c>：供安装脚本调用。
        /// 这样计划任务的细节只有 <see cref="AutoStartManager"/> 一份实现，脚本不必重复拼 XML。
        /// <para>
        /// 注意 <c>--uninstall-autostart</c> 目前在仓库内没有调用方（<c>scripts/install.ps1</c> 只用 install，
        /// 托盘菜单与 <c>autostart</c> 命令直连 <see cref="AutoStartManager"/>）。它是刻意保留的对称开关：
        /// 卸载／清理脚本要注销计划任务时，除此之外没有别的入口。不是漏接线，勿删。
        /// </para>
        /// </summary>
        public static AutoStartCommand AutoStart { get; private set; }

        /// <summary>首个位置参数：配置文件路径。未指定时为 null，由调用方取默认值。</summary>
        public static string? ConfigPath { get; private set; }

        /// <summary>解析命令行参数（<paramref name="args"/> 不含 exe 自身路径）。重复调用会覆盖上一次结果。</summary>
        public static void Parse(string[] args)
        {
            WaitForPid = null;
            ConfigPath = null;
            AutoStart = AutoStartCommand.None;

            if (args == null)
                return;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (string.Equals(arg, "--wait-for-pid", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        // 紧随其后的实参一律视为本选项的值并消费掉，哪怕它不是合法 pid：
                        // 否则损坏的值会掉进下面的位置参数分支被当成配置文件路径，
                        // 导致更新重启后用户的自定义配置悄悄丢失。
                        if (int.TryParse(args[i + 1], out int pid) && pid > 0)
                            WaitForPid = pid;

                        i++;
                    }

                    continue;
                }

                if (string.Equals(arg, "--install-autostart", StringComparison.OrdinalIgnoreCase))
                {
                    AutoStart = AutoStartCommand.Install;
                    continue;
                }

                if (string.Equals(arg, "--uninstall-autostart", StringComparison.OrdinalIgnoreCase))
                {
                    AutoStart = AutoStartCommand.Uninstall;
                    continue;
                }

                // 未知的 "--" 开头参数直接忽略，避免被当成配置文件路径
                if (arg.StartsWith("--", StringComparison.Ordinal))
                    continue;

                ConfigPath ??= arg;
            }
        }
    }
}

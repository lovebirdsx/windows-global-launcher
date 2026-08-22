using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace CommandLauncher
{
    /// <summary>
    /// 开机自启管理：通过「任务计划程序」创建登录触发的计划任务来实现。
    ///
    /// 为什么必须用计划任务而不是 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册表：
    /// 本程序 app.manifest 声明了 requireAdministrator。Windows 登录时由 HKCU\Run 启动一个
    /// requireAdministrator 的程序，不会弹出 UAC，而是**静默失败**，程序根本不会启动。
    /// 唯一可靠的做法是用任务计划程序创建一个「以最高权限运行」的登录触发任务。
    /// 后人容易误改成注册表实现，故此说明必须保留。
    ///
    /// 前提：创建 HighestAvailable（等价 /RL HIGHEST）计划任务本身需要管理员权限。本程序恒以
    /// 管理员运行，正常路径没问题；若将来去掉 requireAdministrator，失败文案里会提示需要管理员权限。
    /// </summary>
    public static class AutoStartManager
    {
        /// <summary>计划任务名。</summary>
        public const string TaskName = "WindowsGlobalLauncher-AutoStart";

        /// <summary>schtasks 执行超时（毫秒）。</summary>
        private const int ProcessTimeoutMs = 15_000;

        // 超时并 Kill 之后，再给输出流的读取任务一点收尾时间。不能无限等，理由见 RunSchTasks 里的注释。
        private const int StreamDrainTimeoutMs = 2_000;

        /// <summary>登录触发延迟 20 秒，避开登录高峰（与程序自身「启动后延迟 30s 检查更新」的克制风格一致）。</summary>
        private const string LogonDelay = "PT20S";

        /// <summary>
        /// 当前是否已配置开机自启。查询失败一律返回 false，绝不抛异常。
        /// 只看 ExitCode 是否为 0，不解析输出文本——中文系统下 schtasks 输出是 GBK，.NET Core
        /// 默认不带该代码页，解析文本容易出乱码或异常。
        /// </summary>
        public static bool IsEnabled()
        {
            try
            {
                return RunSchTasks(new[] { "/Query", "/TN", TaskName }, out _);
            }
            catch (Exception ex)
            {
                // RunSchTasks 已吞异常，这里是最后的兜底
                Logger.LogError("查询开机自启任务状态异常", ex);
                return false;
            }
        }

        /// <summary>
        /// 开启开机自启（幂等：任务已存在则以当前 exe 路径覆盖）。成功返回 true；
        /// 失败返回 false 并给出中文错误说明。
        /// </summary>
        public static bool Enable(out string error)
        {
            error = string.Empty;
            string? tempFile = null;
            try
            {
                // 单文件发布（PublishSingleFile）下 Assembly.Location 是空串，Environment.ProcessPath 才是可靠的 exe 路径。
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    error = "无法获取当前程序路径（Environment.ProcessPath 为空），无法创建开机自启任务";
                    Logger.LogError(error);
                    return false;
                }

                string userId = GetCurrentUserId();
                string xml = BuildTaskXml(exePath, userId);

                // schtasks /XML 只认 Unicode（UTF-16 带 BOM）：用 UTF-8 写盘会报「不是有效的 XML」或中文乱码。
                tempFile = Path.Combine(Path.GetTempPath(), "WindowsGlobalLauncher-AutoStart-" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(tempFile, xml, Encoding.Unicode);

                if (!RunSchTasks(new[] { "/Create", "/TN", TaskName, "/XML", tempFile, "/F" }, out string runError))
                {
                    error = $"{runError}（创建最高权限计划任务需要管理员权限）";
                    Logger.LogWarning($"开启开机自动启动失败：{error}");
                    return false;
                }

                Logger.LogInfo($"已开启开机自动启动：{exePath}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"开启开机自动启动失败：{ex.Message}";
                Logger.LogError("开启开机自动启动异常", ex);
                return false;
            }
            finally
            {
                // 临时 XML 文件用完即删，删除失败静默忽略（临时目录里的残留无碍）
                if (tempFile != null)
                {
                    try { File.Delete(tempFile); } catch { /* 忽略删除失败 */ }
                }
            }
        }

        /// <summary>关闭开机自启（幂等：任务不存在也算成功）。</summary>
        public static bool Disable(out string error)
        {
            error = string.Empty;
            try
            {
                // 任务本来就不存在时 schtasks /Delete 返回非 0，因此先 IsEnabled 判断，不存在直接返回成功。
                if (!IsEnabled())
                {
                    Logger.LogInfo("关闭开机自动启动：任务不存在，无需删除");
                    return true;
                }

                if (!RunSchTasks(new[] { "/Delete", "/TN", TaskName, "/F" }, out string runError))
                {
                    error = runError;
                    Logger.LogWarning($"关闭开机自动启动失败：{runError}");
                    return false;
                }

                Logger.LogInfo("已关闭开机自动启动");
                return true;
            }
            catch (Exception ex)
            {
                error = $"关闭开机自动启动失败：{ex.Message}";
                Logger.LogError("关闭开机自动启动异常", ex);
                return false;
            }
        }

        /// <summary>取当前用户标识：优先 WindowsIdentity 的 SID，取不到回退「域\用户名」。</summary>
        private static string GetCurrentUserId()
        {
            try
            {
                string? sid = WindowsIdentity.GetCurrent().User?.Value;
                if (!string.IsNullOrEmpty(sid))
                {
                    return sid;
                }
            }
            catch
            {
                // 取 SID 失败时回退到「域\用户名」
            }
            return Environment.UserDomainName + "\\" + Environment.UserName;
        }

        /// <summary>
        /// 构造任务计划 XML。路径/用户名在插入 XML 前做 XML 转义（SecurityElement.Escape），
        /// 避免路径里的 &amp; 等字符破坏 XML。
        /// </summary>
        private static string BuildTaskXml(string exePath, string userId)
        {
            string dir = Path.GetDirectoryName(exePath) ?? string.Empty;
            string command = SecurityElement.Escape(exePath);
            string workingDir = SecurityElement.Escape(dir);
            string user = SecurityElement.Escape(userId);

            return $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo>
                    <Description>WindowsGlobalLauncher 开机自启（登录后延迟启动、最高权限运行）</Description>
                  </RegistrationInfo>
                  <Triggers>
                    <LogonTrigger>
                      <Enabled>true</Enabled>
                      <Delay>{LogonDelay}</Delay>
                      <UserId>{user}</UserId>
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>{user}</UserId>
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>HighestAvailable</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>false</AllowHardTerminate>
                    <StartWhenAvailable>true</StartWhenAvailable>
                    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                    <IdleSettings>
                      <StopOnIdleEnd>true</StopOnIdleEnd>
                      <RestartOnIdle>false</RestartOnIdle>
                    </IdleSettings>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>false</Hidden>
                    <RunOnlyIfIdle>false</RunOnlyIfIdle>
                    <WakeToRun>false</WakeToRun>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>7</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{command}</Command>
                      <WorkingDirectory>{workingDir}</WorkingDirectory>
                    </Exec>
                  </Actions>
                </Task>
                """;
        }

        /// <summary>
        /// 运行 schtasks.exe 并等待退出。参数用 ArgumentList 逐个添加（不手工拼带引号的命令行，
        /// exe 路径可能含空格）。成功（退出码 0）返回 true；失败/超时/异常返回 false 并给出中文错误说明。
        /// 绝不抛异常。
        /// </summary>
        private static bool RunSchTasks(string[] args, out string error)
        {
            error = string.Empty;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var arg in args)
                {
                    startInfo.ArgumentList.Add(arg);
                }

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                // 并行异步读取两个流：schtasks 输出通常很小，但同步顺序读在极端情况下会因管道缓冲区填满而互相等待。
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(ProcessTimeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* 忽略 Kill 失败 */ }

                    // 这里必须限时：ReadToEndAsync 只有进程退出、管道关闭才完成，
                    // 万一 Kill 失败而 schtasks 仍僵死着，无限的 WaitAll 会把调用线程一起挂住——
                    // 而 IsEnabled() 是从 UI 线程（构造、托盘菜单 Opening）同步调用的，那就是界面冻结。
                    // 超时路径本来就丢弃输出，等不到就直接走失败分支；
                    // 用 try/catch 吞掉读取异常，避免遗留未观察的 Task 异常刷 ERROR 日志。
                    try { Task.WaitAll(new Task[] { stdoutTask, stderrTask }, StreamDrainTimeoutMs); }
                    catch { /* 读取失败无所谓，输出在超时路径本就不使用 */ }

                    error = $"schtasks 执行超时（超过 {ProcessTimeoutMs / 1000} 秒）";
                    Logger.LogError(error);
                    return false;
                }

                string stdout = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();

                if (process.ExitCode == 0)
                {
                    return true;
                }

                string detail = (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
                error = string.IsNullOrEmpty(detail)
                    ? $"schtasks 失败（退出码 {process.ExitCode}）"
                    : $"schtasks 失败（退出码 {process.ExitCode}）：{detail}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"调用 schtasks 失败：{ex.Message}";
                Logger.LogError("调用 schtasks 异常", ex);
                return false;
            }
        }
    }
}

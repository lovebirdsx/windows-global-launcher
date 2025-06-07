using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CommandLauncher
{
    public static class Logger
    {
        private static readonly string LogDirectory = Path.Combine(App.BaseDir, "Logs");
        private static readonly string LogFileName = $"app_{DateTime.Now:yyyyMMdd}.log";
        private static readonly string LogFilePath = Path.Combine(LogDirectory, LogFileName);

        static Logger()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }
            }
            catch
            {
                // 忽略创建日志目录失败的错误
            }

            // 最多保留3天的日志文件
            try
            {
                var logFiles = Directory.GetFiles(LogDirectory, "app_*.log");
                foreach (var file in logFiles)
                {
                    if (File.GetCreationTime(file) < DateTime.Now.AddDays(-3))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // 忽略删除日志文件失败的错误
                        }
                    }
                }
            }
            catch
            {
                // 忽略清理旧日志文件失败的错误
            }
        }

        public static void OpenLogFile()
        {
            try
            {
                string logFilePath = GetLogFilePath();
                if (File.Exists(logFilePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = logFilePath,
                        UseShellExecute = true
                    });
                    LogInfo("打开日志文件");
                }
                else
                {
                    MessageBox.Show("日志文件不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                LogError("打开日志文件失败", ex);
                MessageBox.Show($"打开日志文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static string GetLogFilePath() => LogFilePath;

        public static void LogInfo(string message)
        {
            WriteLog("INFO", message);
        }

        public static void LogWarning(string message)
        {
            WriteLog("WARN", message);
        }

        public static void LogError(string message, Exception ex)
        {
            var fullMessage = $"{message}: {ex}";
            WriteLog("ERROR", fullMessage);
        }

        private static void WriteLog(string level, string message)
        {
            try
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);
            }
            catch
            {
                // 忽略写入日志失败的错误
            }
        }
    }
}

using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 命令行参数解析测试。
    /// 重点覆盖「自动更新重启参数不能被误当成配置文件路径」——历史上 MainWindow 直接取 args[1] 当配置路径，
    /// 引入 --wait-for-pid 后若解析有误，用户的配置文件会在每次自动更新后悄悄丢失。
    /// </summary>
    public class StartupArgsTests
    {
        [Fact]
        public void Parse_NoArgs_BothEmpty()
        {
            StartupArgs.Parse([]);

            Assert.Null(StartupArgs.WaitForPid);
            Assert.Null(StartupArgs.ConfigPath);
        }

        [Fact]
        public void Parse_OnlyConfigPath_ReadsConfigPath()
        {
            StartupArgs.Parse([@"D:\my\config.json"]);

            Assert.Null(StartupArgs.WaitForPid);
            Assert.Equal(@"D:\my\config.json", StartupArgs.ConfigPath);
        }

        [Fact]
        public void Parse_OnlyWaitForPid_DoesNotLeakIntoConfigPath()
        {
            StartupArgs.Parse(["--wait-for-pid", "1234"]);

            Assert.Equal(1234, StartupArgs.WaitForPid);
            Assert.Null(StartupArgs.ConfigPath); // 关键：pid 参数绝不能被当成配置文件路径
        }

        // 注意：xunit 的 InlineData 是 params object[]，直接传 new[]{"a","b"} 会被展开成多个实参，
        // 与 string[] 形参不匹配。故这里用 '|' 分隔的单个字符串表达参数列表，测试内再拆开。
        [Theory]
        [InlineData(@"--wait-for-pid|4321|C:\cfg.json")]
        [InlineData(@"C:\cfg.json|--wait-for-pid|4321")]
        public void Parse_BothArgs_OrderIndependent(string joinedArgs)
        {
            StartupArgs.Parse(joinedArgs.Split('|'));

            Assert.Equal(4321, StartupArgs.WaitForPid);
            Assert.Equal(@"C:\cfg.json", StartupArgs.ConfigPath);
        }

        [Theory]
        [InlineData("--wait-for-pid")]          // 缺少 pid 值
        [InlineData("--wait-for-pid|abc")]      // pid 非数字
        [InlineData("--wait-for-pid|0")]        // pid 非正数
        [InlineData("--wait-for-pid|-5")]
        public void Parse_InvalidPid_IgnoredAndNotTreatedAsConfigPath(string joinedArgs)
        {
            StartupArgs.Parse(joinedArgs.Split('|'));

            Assert.Null(StartupArgs.WaitForPid);
            Assert.Null(StartupArgs.ConfigPath);
        }

        [Fact]
        public void Parse_UnknownOption_Ignored()
        {
            StartupArgs.Parse(["--unknown", @"C:\cfg.json"]);

            Assert.Null(StartupArgs.WaitForPid);
            Assert.Equal(@"C:\cfg.json", StartupArgs.ConfigPath); // 未知开关被跳过，位置参数仍能取到
        }

        [Fact]
        public void Parse_MultiplePositionalArgs_FirstWins()
        {
            StartupArgs.Parse([@"C:\first.json", @"C:\second.json"]);

            Assert.Equal(@"C:\first.json", StartupArgs.ConfigPath);
        }

        [Fact]
        public void Parse_IsCaseInsensitiveForOption()
        {
            StartupArgs.Parse(["--WAIT-FOR-PID", "77"]);

            Assert.Equal(77, StartupArgs.WaitForPid);
        }

        [Fact]
        public void Parse_ResetsPreviousResult()
        {
            StartupArgs.Parse(["--wait-for-pid", "10", @"C:\a.json"]);
            StartupArgs.Parse([]);

            Assert.Null(StartupArgs.WaitForPid);
            Assert.Null(StartupArgs.ConfigPath);
        }
    }
}

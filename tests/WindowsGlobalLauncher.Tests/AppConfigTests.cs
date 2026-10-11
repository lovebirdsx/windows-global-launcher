using System.Text.Json;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 配置 JSON 拼装（<see cref="AppConfig.ComposeConfigJson"/>）的纯函数测试。
    /// 只构造 Config 对象，不触碰 AppConfig.Instance（单例会读写真实数据目录并启动 FileSystemWatcher）。
    /// </summary>
    public class AppConfigTests
    {
        private static readonly JsonSerializerOptions LoadOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static Config RoundTrip(Config config)
            => JsonSerializer.Deserialize<Config>(AppConfig.ComposeConfigJson(config), LoadOptions)!;

        [Fact]
        public void ComposeConfigJson_EscapesBackslashesAndQuotes()
        {
            // 核心断言：Windows 路径里的反斜杠与引号必须被正确转义。
            // 旧版直接字符串拼插会写出非法 JSON，下次加载解析失败会清空用户配置。
            var config = new Config
            {
                HotKey = "Ctrl+Shift+I",
                Commands =
                [
                    new()
                    {
                        Name = "带\"引号\"的命令",
                        Description = "路径 C:\\Program Files\\子目录",
                        Shell = "C:\\Program Files\\\"a\"\\x.exe",
                    },
                ],
            };

            var reloaded = RoundTrip(config);

            Assert.Single(reloaded.Commands);
            Assert.Equal("带\"引号\"的命令", reloaded.Commands[0].Name);
            Assert.Equal("路径 C:\\Program Files\\子目录", reloaded.Commands[0].Description);
            Assert.Equal("C:\\Program Files\\\"a\"\\x.exe", reloaded.Commands[0].Shell);
        }

        [Fact]
        public void ComposeConfigJson_KeepsChineseReadable()
        {
            // 中文不以 \uXXXX 形式落盘，保证用户直接编辑配置文件的可读性
            var config = new Config
            {
                Commands = [new() { Name = "记事本", Description = "打开Windows记事本", Shell = "notepad.exe" }],
                WindowActions = [new() { Action = "Screenshot", HotKey = "F4" }],
                KeyMappingProfiles = [new() { Name = "星际争霸", Mappings = [new() { From = "Z", To = "F2" }] }],
            };

            var json = AppConfig.ComposeConfigJson(config);

            Assert.Contains("记事本", json);
            Assert.Contains("星际争霸", json);
            Assert.Contains("\"Screenshot\"", json);
            Assert.DoesNotContain("\\u", json);
        }

        [Fact]
        public void ComposeConfigJson_RoundTripsAllFields()
        {
            var config = new Config
            {
                MaxDisplayItems = 20,
                HotKey = "Ctrl+Alt+Shift+Win+Q",
                WindowActions =
                [
                    new() { Action = "CloseWindow", HotKey = "Alt+Q", Enabled = true },
                    new() { Action = "EyeCareOffice", HotKey = "", Enabled = false },
                    new() { Action = "PinBoxSelect", HotKey = "Win+Q", Enabled = true },
                ],
                Commands =
                [
                    new() { Name = "记事本", Description = "打开记事本\n第二行", Shell = "notepad.exe", HotKey = "F5", RunAsAdmin = true },
                    new() { Name = "看图", Description = "查看图片", Shell = "mspaint.exe", HotKey = null, RunAsAdmin = false },
                ],
            };

            var reloaded = RoundTrip(config);

            Assert.Equal(20, reloaded.MaxDisplayItems);
            Assert.Equal("Ctrl+Alt+Shift+Win+Q", reloaded.HotKey);
            Assert.Equal(3, reloaded.WindowActions.Count);
            // 顺序保持
            Assert.Equal("CloseWindow", reloaded.WindowActions[0].Action);
            Assert.Equal("EyeCareOffice", reloaded.WindowActions[1].Action);
            Assert.Equal("PinBoxSelect", reloaded.WindowActions[2].Action);
            Assert.True(reloaded.WindowActions[0].Enabled);
            Assert.False(reloaded.WindowActions[1].Enabled);
            Assert.Equal(2, reloaded.Commands.Count);
            Assert.Equal("记事本", reloaded.Commands[0].Name);
            Assert.Equal("打开记事本\n第二行", reloaded.Commands[0].Description);
            Assert.True(reloaded.Commands[0].RunAsAdmin);
            Assert.Equal("", reloaded.Commands[1].HotKey); // null 归一为空串
            Assert.False(reloaded.Commands[1].RunAsAdmin);
        }

        [Fact]
        public void ComposeConfigJson_EmptyLists_ProducesParsableJson()
        {
            var reloaded = RoundTrip(new Config
            {
                HotKey = "Ctrl+Shift+I",
                Commands = [],
                WindowActions = [],
                KeyMappingProfiles = [],
            });

            Assert.Empty(reloaded.Commands);
            Assert.Empty(reloaded.WindowActions);
            Assert.Empty(reloaded.KeyMappingProfiles);
            Assert.Equal("Ctrl+Shift+I", reloaded.HotKey);
        }

        [Fact]
        public void ComposeConfigJson_NullStrings_BecomeEmpty()
        {
            var reloaded = RoundTrip(new Config
            {
                HotKey = null!,
                WindowActions = [new() { Action = "Screenshot", HotKey = null! }],
                Commands = [new() { Name = null!, Description = null!, Shell = null!, HotKey = null }],
            });

            Assert.Equal("", reloaded.HotKey);
            Assert.Equal("", reloaded.WindowActions[0].HotKey);
            Assert.Equal("", reloaded.Commands[0].Name);
        }

        [Fact]
        public void ComposeConfigJson_RoundTripsKeyMappingProfiles()
        {
            // 嵌套两层数组（方案 → 映射）的手写拼接往返：
            // 顺序、中文名、启用状态、进程列表、空 Processes（全局）与空 Mappings 都必须保持
            var config = new Config
            {
                KeyMappingProfiles =
                [
                    new()
                    {
                        Name = "星际争霸",
                        Enabled = true,
                        Processes = ["StarCraft.exe", "StarCraft1.exe"],
                        Mappings =
                        [
                            new() { From = "Z", To = "F2" },
                            new() { From = "X", To = "F3" },
                        ],
                    },
                    new()
                    {
                        Name = "全局备用",
                        Enabled = false,
                        Processes = [],
                        Mappings = [new() { From = "C", To = "F4" }],
                    },
                    new()
                    {
                        Name = "空方案",
                        Processes = [],
                        Mappings = [],
                    },
                ],
            };

            var reloaded = RoundTrip(config);

            Assert.Equal(3, reloaded.KeyMappingProfiles.Count);
            Assert.Equal("星际争霸", reloaded.KeyMappingProfiles[0].Name);
            Assert.True(reloaded.KeyMappingProfiles[0].Enabled);
            Assert.Equal(2, reloaded.KeyMappingProfiles[0].Processes.Count);
            Assert.Equal("StarCraft.exe", reloaded.KeyMappingProfiles[0].Processes[0]);
            Assert.Equal(2, reloaded.KeyMappingProfiles[0].Mappings.Count);
            Assert.Equal("Z", reloaded.KeyMappingProfiles[0].Mappings[0].From);
            Assert.Equal("F2", reloaded.KeyMappingProfiles[0].Mappings[0].To);
            Assert.Equal("全局备用", reloaded.KeyMappingProfiles[1].Name);
            Assert.False(reloaded.KeyMappingProfiles[1].Enabled);
            Assert.Empty(reloaded.KeyMappingProfiles[1].Processes);
            Assert.Empty(reloaded.KeyMappingProfiles[2].Mappings);
        }
    }
}

using System.Collections.Generic;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 按键映射的校验构建与匹配（<see cref="KeyMapping"/>）纯函数测试。
    /// 只构造配置/运行时对象，不触碰 AppConfig.Instance、键盘钩子与真实前台进程。
    /// </summary>
    public class KeyMappingTests
    {
        // VK 常量（测试锁定行为，不依赖 HotKeyParser 的实现细节）
        private const int VK_Z = 0x5A;
        private const int VK_X = 0x58;
        private const int VK_C = 0x43;
        private const int VK_F2 = 0x71;
        private const int VK_F3 = 0x72;
        private const int VK_F4 = 0x73;

        // ---------- BuildProfile：校验与构建 ----------

        [Fact]
        public void BuildProfile_ValidMappings_BuildsRuntime()
        {
            var profile = new ConfigKeyMappingProfile
            {
                Name = "星际争霸",
                Processes = ["StarCraft.exe"],
                Mappings =
                [
                    new() { From = "Z", To = "F2" },
                    new() { From = "X", To = "F3" },
                ],
            };

            var runtime = KeyMapping.BuildProfile(profile);

            Assert.NotNull(runtime);
            Assert.Equal("星际争霸", runtime!.Name);
            Assert.Single(runtime.Processes);
            Assert.Equal(VK_F2, runtime.Mappings[VK_Z]);
            Assert.Equal(VK_F3, runtime.Mappings[VK_X]);
        }

        [Fact]
        public void BuildProfile_KeyNamesAreCaseInsensitive_AndDuplicateKeepsFirst()
        {
            // "z" 与 "Z" 解析为同一 vk：视为重复源键，保留首条（Z→F2 生效，Z→F3 跳过）
            var profile = new ConfigKeyMappingProfile
            {
                Name = "重复键",
                Mappings =
                [
                    new() { From = "Z", To = "F2" },
                    new() { From = "z", To = "F3" },
                ],
            };

            var runtime = KeyMapping.BuildProfile(profile);

            Assert.NotNull(runtime);
            Assert.Single(runtime!.Mappings);
            Assert.Equal(VK_F2, runtime.Mappings[VK_Z]);
        }

        [Fact]
        public void BuildProfile_InvalidEntries_AreSkipped()
        {
            // 带修饰键的源/目标、源目标相同、未知键名，全部跳过；有效条目保留
            var profile = new ConfigKeyMappingProfile
            {
                Name = "混合",
                Mappings =
                [
                    new() { From = "Ctrl+F2", To = "X" },    // 源键带修饰键 → 跳过
                    new() { From = "X", To = "Alt+C" },      // 目标键带修饰键 → 跳过
                    new() { From = "C", To = "C" },          // 源目标相同 → 跳过
                    new() { From = "不存在的键", To = "F2" },  // 未知键名 → 跳过
                    new() { From = "Z", To = "F2" },         // 有效
                ],
            };

            var runtime = KeyMapping.BuildProfile(profile);

            Assert.NotNull(runtime);
            Assert.Single(runtime!.Mappings);
            Assert.Equal(VK_F2, runtime.Mappings[VK_Z]);
        }

        [Fact]
        public void BuildProfile_AllInvalidOrEmpty_ReturnsNull()
        {
            Assert.Null(KeyMapping.BuildProfile(new ConfigKeyMappingProfile { Name = "空", Mappings = [] }));
            Assert.Null(KeyMapping.BuildProfile(new ConfigKeyMappingProfile
            {
                Name = "全非法",
                Mappings = [new() { From = "Z", To = "Z" }],
            }));
        }

        [Fact]
        public void BuildProfile_BlankProcessNames_AreRemoved()
        {
            var profile = new ConfigKeyMappingProfile
            {
                Name = "空白名单项",
                Processes = [" ", "", "StarCraft.exe"],
                Mappings = [new() { From = "Z", To = "F2" }],
            };

            var runtime = KeyMapping.BuildProfile(profile);

            Assert.NotNull(runtime);
            Assert.Single(runtime!.Processes);
            Assert.Equal("StarCraft.exe", runtime.Processes[0]);
        }

        // ---------- TryResolve：匹配 ----------

        [Fact]
        public void TryResolve_ProcessWhitelist_MatchesCaseInsensitive()
        {
            var profiles = new List<KeyMappingProfileRuntime> { MakeProfile("A", ["StarCraft.exe"], (VK_Z, VK_F2)) };

            Assert.True(KeyMapping.TryResolve(profiles, "starcraft.exe", VK_Z, out int target));
            Assert.Equal(VK_F2, target);

            // 白名单外的进程不命中
            Assert.False(KeyMapping.TryResolve(profiles, "notepad.exe", VK_Z, out _));
        }

        [Fact]
        public void TryResolve_EmptyProcessList_IsGlobal()
        {
            var profiles = new List<KeyMappingProfileRuntime> { MakeProfile("全局", [], (VK_Z, VK_F2)) };

            Assert.True(KeyMapping.TryResolve(profiles, "任何进程.exe", VK_Z, out int target));
            Assert.Equal(VK_F2, target);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void TryResolve_EmptyProcessName_NeverMatches(string? processName)
        {
            // 无前台 / 前台为本程序自身 / 解析失败 → 一律不命中（fail-open 透传）
            var profiles = new List<KeyMappingProfileRuntime> { MakeProfile("全局", [], (VK_Z, VK_F2)) };

            Assert.False(KeyMapping.TryResolve(profiles, processName, VK_Z, out _));
        }

        [Fact]
        public void TryResolve_FirstMatchingProfileWins()
        {
            // 两个方案都命中同一进程与源键：按配置顺序取第一个（顺序即优先级）
            var profiles = new List<KeyMappingProfileRuntime>
            {
                MakeProfile("先", ["game.exe"], (VK_Z, VK_F2)),
                MakeProfile("后", ["game.exe"], (VK_Z, VK_F3)),
            };

            Assert.True(KeyMapping.TryResolve(profiles, "game.exe", VK_Z, out int target));
            Assert.Equal(VK_F2, target);
        }

        [Fact]
        public void TryResolve_NonMappedKey_ReturnsFalse()
        {
            var profiles = new List<KeyMappingProfileRuntime> { MakeProfile("A", [], (VK_Z, VK_F2)) };

            Assert.False(KeyMapping.TryResolve(profiles, "game.exe", VK_X, out _));
        }

        // ---------- IsExtendedKey ----------

        [Theory]
        [InlineData(0x21)] // PageUp
        [InlineData(0x22)] // PageDown
        [InlineData(0x23)] // End
        [InlineData(0x24)] // Home
        [InlineData(0x25)] // Left
        [InlineData(0x26)] // Up
        [InlineData(0x27)] // Right
        [InlineData(0x28)] // Down
        [InlineData(0x2D)] // Insert
        [InlineData(0x2E)] // Delete
        public void IsExtendedKey_TrueForExtendedKeys(int vk) => Assert.True(KeyMappingInjection.IsExtendedKey(vk));

        [Theory]
        [InlineData(0x41)] // A
        [InlineData(0x31)] // 1
        [InlineData(0x70)] // F1
        [InlineData(0x0D)] // Enter
        [InlineData(0x09)] // Tab
        [InlineData(0x20)] // Space
        [InlineData(0xBC)] // 逗号
        public void IsExtendedKey_FalseForNormalKeys(int vk) => Assert.False(KeyMappingInjection.IsExtendedKey(vk));

        private static KeyMappingProfileRuntime MakeProfile(string name, string[] processes, params (int Source, int Target)[] mappings)
        {
            var dict = new Dictionary<int, int>();
            foreach (var (source, target) in mappings)
                dict[source] = target;

            return new KeyMappingProfileRuntime
            {
                Name = name,
                Processes = processes,
                Mappings = dict
            };
        }
    }
}

using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 输入法切换手势的纯逻辑测试（<see cref="ImeSwitchChordTracker"/>）。
    /// 不涉及低级键盘钩子与真实按键事件；键入触发已移除。
    /// </summary>
    public class ImeTriggerTests
    {
        // 虚拟键码常量（测试锁定行为，不依赖 HotKeyParser/KeyTable）
        private const int VK_TAB = 0x09;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_SPACE = 0x20;
        private const int VK_RIGHT = 0x27;
        private const int VK_A = 0x41;
        private const int VK_C = 0x43;
        private const int VK_LWIN = 0x5B;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;

        // ---------- ImeSwitchChordTracker ----------

        [Fact]
        public void ShiftAlone_TriggersOnRelease()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_SHIFT);

            Assert.True(tracker.OnKeyUp(VK_SHIFT));
        }

        [Fact]
        public void ShiftWithLetter_DoesNotTrigger()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_SHIFT);
            tracker.OnKeyDown(VK_A);
            tracker.OnKeyUp(VK_A);

            Assert.False(tracker.OnKeyUp(VK_SHIFT));
        }

        [Fact]
        public void CtrlSpace_TriggersOnSpacePress()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_CONTROL);

            // IME 在空格按下时完成切换：按下即触发，不必等松开
            Assert.True(tracker.OnKeyDown(VK_SPACE));
            // 空格松开不重复触发
            Assert.False(tracker.OnKeyUp(VK_SPACE));
            // Ctrl 自身松开不重复触发
            Assert.False(tracker.OnKeyUp(VK_CONTROL));
        }

        [Fact]
        public void WinSpace_TriggersOnSpacePress()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_LWIN);

            Assert.True(tracker.OnKeyDown(VK_SPACE));
            Assert.False(tracker.OnKeyUp(VK_SPACE));
            // 单独按 Win 不提示
            Assert.False(tracker.OnKeyUp(VK_LWIN));
        }

        [Fact]
        public void AltHeld_ShiftTap_StillTriggers()
        {
            // Shift 没有与其它键组合（只是 Alt 还按着），松开 Shift 仍应触发
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_MENU);
            tracker.OnKeyDown(VK_LSHIFT);

            Assert.True(tracker.OnKeyUp(VK_LSHIFT));
        }

        [Fact]
        public void CtrlWithLetter_DoesNotTrigger()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_CONTROL);
            tracker.OnKeyDown(VK_C);
            tracker.OnKeyUp(VK_C);

            Assert.False(tracker.OnKeyUp(VK_CONTROL));
        }

        [Fact]
        public void CtrlShiftWithLetter_DoesNotTrigger()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_CONTROL);
            tracker.OnKeyDown(VK_SHIFT);
            tracker.OnKeyDown(VK_C);
            tracker.OnKeyUp(VK_C);

            Assert.False(tracker.OnKeyUp(VK_SHIFT));
            Assert.False(tracker.OnKeyUp(VK_CONTROL));
        }

        [Fact]
        public void AltTab_DoesNotTrigger()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_MENU);
            tracker.OnKeyDown(VK_TAB);
            tracker.OnKeyUp(VK_TAB);

            Assert.False(tracker.OnKeyUp(VK_MENU));
        }

        [Fact]
        public void ShiftWithArrow_DoesNotTrigger()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_SHIFT);
            tracker.OnKeyDown(VK_RIGHT);
            tracker.OnKeyUp(VK_RIGHT);

            Assert.False(tracker.OnKeyUp(VK_SHIFT));
        }

        [Fact]
        public void AutoRepeatLetter_DoesNotTrigger()
        {
            // 按住 A 产生的自动重复不应改变判定
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_SHIFT);
            tracker.OnKeyDown(VK_A);
            tracker.OnKeyDown(VK_A);
            tracker.OnKeyDown(VK_A);
            tracker.OnKeyUp(VK_A);

            Assert.False(tracker.OnKeyUp(VK_SHIFT));
        }

        [Fact]
        public void Reset_ClearsCtrlState()
        {
            // 重置后 Ctrl 不再被视为按住：随后的普通空格不应触发。
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_CONTROL);
            tracker.Reset();

            Assert.False(tracker.OnKeyDown(VK_SPACE));
            Assert.False(tracker.AnyCtrlAltWinHeld);
        }

        [Fact]
        public void BothShiftVariants_ReleasingOne_DoesNotTrigger()
        {
            // 左右 Shift 同时按住：松开第一个不算「独立点击」
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_LSHIFT);
            tracker.OnKeyDown(VK_RSHIFT);

            Assert.False(tracker.OnKeyUp(VK_LSHIFT));
            Assert.True(tracker.OnKeyUp(VK_RSHIFT));
        }

        // ---------- 自动重复 / 缺失 keyup / 物理状态自愈 ----------

        [Theory]
        [InlineData(VK_SHIFT, true)]     // 单独 Shift 松开应触发
        [InlineData(VK_CONTROL, true)]   // 单独 Ctrl 松开应触发
        [InlineData(VK_MENU, true)]      // 单独 Alt 松开应触发
        [InlineData(VK_LWIN, false)]     // 单独 Win 不提示
        public void ModifierAutoRepeat_ReleasesCleanly(int vk, bool triggersOnRelease)
        {
            // 按住修饰键超过 typematic 延迟会不断收到 keydown；
            // 每个 keydown 都累加计数会让一次物理松开无法归零，后续输入全部失效。
            var tracker = new ImeSwitchChordTracker();

            for (int i = 0; i < 8; i++)
                tracker.OnKeyDown(vk);

            Assert.True(tracker.HasTrackedState);

            Assert.Equal(triggersOnRelease, tracker.OnKeyUp(vk));
            Assert.False(tracker.HasTrackedState);
            Assert.False(tracker.AnyCtrlAltWinHeld);
        }

        [Fact]
        public void KeyUpWithoutKeyDown_DoesNotTrigger()
        {
            // 钩子刚装上时用户可能正按着键：只看到 keyup 不能凭空触发一次检查
            var tracker = new ImeSwitchChordTracker();

            Assert.False(tracker.OnKeyUp(VK_CONTROL));
            Assert.False(tracker.OnKeyUp(VK_LSHIFT));
            Assert.False(tracker.AnyCtrlAltWinHeld);
            Assert.False(tracker.HasTrackedState);
        }

        [Fact]
        public void GenericModifierVk_KeyDownSpecificKeyUp()
        {
            // 不同键盘可能 keydown 报通用 VK、keyup 报左右变体
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_CONTROL);

            Assert.True(tracker.OnKeyUp(VK_LCONTROL));
            Assert.False(tracker.AnyCtrlAltWinHeld);
        }

        [Fact]
        public void SpecificModifierVk_KeyDownGenericKeyUp()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_LCONTROL);

            Assert.True(tracker.OnKeyUp(VK_CONTROL));
            Assert.False(tracker.AnyCtrlAltWinHeld);
        }

        [Fact]
        public void BothCtrlVariants_ReleasingOne_DoesNotTrigger()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_LCONTROL);
            tracker.OnKeyDown(VK_RCONTROL);

            Assert.False(tracker.OnKeyUp(VK_LCONTROL));
            Assert.True(tracker.OnKeyUp(VK_RCONTROL));
        }

        [Fact]
        public void Synchronize_ClearsStaleModifier()
        {
            // Alt+Tab、Ctrl+滚轮、安全桌面等场景可能丢失 keyup：
            // 物理状态核对发现键已松开时应自愈，后续输入不再被误判为组合键。
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_CONTROL);
            Assert.True(tracker.AnyCtrlAltWinHeld);

            tracker.Synchronize(shiftDown: false, ctrlDown: false, altDown: false, winDown: false);

            Assert.False(tracker.AnyCtrlAltWinHeld);
            Assert.False(tracker.HasTrackedState);

            // 自愈后单独的 Shift 点击仍应触发
            tracker.OnKeyDown(VK_SHIFT);
            Assert.True(tracker.OnKeyUp(VK_SHIFT));
        }

        [Fact]
        public void Synchronize_KeepsPhysicallyHeldFamily()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_CONTROL);
            tracker.Synchronize(shiftDown: false, ctrlDown: true, altDown: false, winDown: false);

            Assert.True(tracker.AnyCtrlAltWinHeld);
            Assert.True(tracker.OnKeyUp(VK_CONTROL));
        }

        [Fact]
        public void Synchronize_ClearsCtrlState()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_CONTROL);
            tracker.Synchronize(shiftDown: false, ctrlDown: false, altDown: false, winDown: false);

            // 物理上 Ctrl 已松开：随后的普通空格不再视为 Ctrl+Space。
            Assert.False(tracker.OnKeyDown(VK_SPACE));
            Assert.False(tracker.AnyCtrlAltWinHeld);
        }

        [Fact]
        public void CtrlReleasedBeforeSpace_TriggersOnceOnPress()
        {
            // 用户实测的常见松键顺序：Space 按下后先松 Ctrl、再松 Space。
            // 触发发生在按下时，松键顺序不影响结果，且不会重复触发。
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_CONTROL);

            Assert.True(tracker.OnKeyDown(VK_SPACE));   // 按下即触发
            Assert.False(tracker.OnKeyUp(VK_CONTROL));  // Ctrl 已参与组合，不触发
            Assert.False(tracker.OnKeyUp(VK_SPACE));    // 空格松开不重复
        }

        [Fact]
        public void SpaceFirstThenCtrl_TriggersOnCtrlRelease()
        {
            // 空格先按下时尚未构成组合（IME 不会切换）；随后按 Ctrl、松开 Ctrl
            // 按「独立修饰键点击」触发一次（既有语义）。调用方会 Stop/Start 合并计时器，
            // 不会重复弹角标。
            var tracker = new ImeSwitchChordTracker();

            Assert.False(tracker.OnKeyDown(VK_SPACE));    // 先按空格：尚无修饰键
            Assert.False(tracker.OnKeyDown(VK_CONTROL));  // Ctrl 后按
            Assert.False(tracker.OnKeyUp(VK_SPACE));      // 空格松开不触发
            Assert.True(tracker.OnKeyUp(VK_CONTROL));     // 独立 Ctrl 点击 → 触发一次探测
        }

        [Fact]
        public void TwoConsecutiveCtrlSpace_EachTriggersOnce()
        {
            var tracker = new ImeSwitchChordTracker();

            for (int i = 0; i < 2; i++)
            {
                tracker.OnKeyDown(VK_CONTROL);
                Assert.True(tracker.OnKeyDown(VK_SPACE));
                Assert.False(tracker.OnKeyUp(VK_SPACE));
                Assert.False(tracker.OnKeyUp(VK_CONTROL));
            }
        }

        [Fact]
        public void PlainSpace_DoesNotTrigger()
        {
            var tracker = new ImeSwitchChordTracker();

            Assert.False(tracker.OnKeyDown(VK_SPACE));
            Assert.False(tracker.OnKeyUp(VK_SPACE));
        }

        [Fact]
        public void ShiftSpace_DoesNotTrigger()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_LSHIFT);
            Assert.False(tracker.OnKeyDown(VK_SPACE));
            Assert.False(tracker.OnKeyUp(VK_SPACE));
            // Shift 参与了组合，松开不触发
            Assert.False(tracker.OnKeyUp(VK_LSHIFT));
        }

        [Fact]
        public void SpaceAutoRepeat_TriggersEachDownButNotOnUp()
        {
            // 按住空格会不断收到 keydown：每次都返回 true，调用方据此重启延迟计时器，
            // 从而在按住期间推迟探测、松手后只查一次。
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_CONTROL);
            for (int i = 0; i < 5; i++)
                Assert.True(tracker.OnKeyDown(VK_SPACE));

            Assert.False(tracker.OnKeyUp(VK_SPACE));
            Assert.False(tracker.OnKeyUp(VK_CONTROL));
        }

        [Fact]
        public void Reset_AfterAutoRepeat_WorksAgain()
        {
            var tracker = new ImeSwitchChordTracker();

            for (int i = 0; i < 4; i++)
                tracker.OnKeyDown(VK_LSHIFT);
            tracker.Reset();

            Assert.False(tracker.HasTrackedState);

            tracker.OnKeyDown(VK_LSHIFT);
            Assert.True(tracker.OnKeyUp(VK_LSHIFT));
        }

        // ---------- AnyCtrlAltWinHeld ----------

        [Fact]
        public void AnyCtrlAltWinHeld_ShiftOnly_ReturnsFalse()
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(VK_SHIFT);

            Assert.False(tracker.AnyCtrlAltWinHeld);
        }

        [Theory]
        [InlineData(VK_CONTROL)]
        [InlineData(VK_MENU)]
        [InlineData(VK_LWIN)]
        public void AnyCtrlAltWinHeld_CtrlAltOrWin_ReturnsTrue(int vk)
        {
            var tracker = new ImeSwitchChordTracker();

            tracker.OnKeyDown(vk);

            Assert.True(tracker.AnyCtrlAltWinHeld);
        }
    }
}

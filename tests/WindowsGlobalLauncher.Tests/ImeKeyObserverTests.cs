using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 键盘观察编排（<see cref="ImeKeyObserver"/>）的纯逻辑测试。
    /// 重点锁定修饰键物理状态自愈与当前按键事件的先后顺序。
    /// </summary>
    public class ImeKeyObserverTests
    {
        private const int VK_A = 0x41;
        private const int VK_B = 0x42;
        private const int VK_SHIFT = 0x10;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_LMENU = 0xA4;
        private const int VK_CONTROL = 0x11;
        private const int VK_SPACE = 0x20;

        private sealed class PhysicalState
        {
            public bool Shift;
            public bool Ctrl;
            public bool Alt;
            public bool Win = false; // 显式初值：测试从不设置它，避免 CS0649
            public int Reads;

            public ImeModifierState Read()
            {
                Reads++;
                return new ImeModifierState(Shift, Ctrl, Alt, Win);
            }
        }

        [Fact]
        public void ShiftTap_TriggersOnRelease()
        {
            var state = new PhysicalState();
            var observer = new ImeKeyObserver(new ImeSwitchChordTracker(), state.Read);

            Assert.False(observer.OnKey(VK_SHIFT, isDown: true, now: 10_000));
            Assert.True(observer.OnKey(VK_SHIFT, isDown: false, now: 10_100));
        }

        [Fact]
        public void LostKeyUp_SelfHealsBeforeCurrentEvent()
        {
            // Ctrl 的 keyup 丢失：追踪器仍认为 Ctrl 按住。
            // 下一次按下 Shift 时，自愈必须在喂入 Shift 之前完成，
            // 才能清掉陈旧的 Ctrl 而不会误清新按下的 Shift。
            var state = new PhysicalState { Ctrl = true };
            var observer = new ImeKeyObserver(new ImeSwitchChordTracker(), state.Read);

            Assert.False(observer.OnKey(0x11, isDown: true, now: 10_000)); // Ctrl down

            state.Ctrl = false; // 物理已松开，但 keyup 丢失
            state.Shift = false; // 钩子回调先于状态表更新：此刻 Shift 还未被系统标记为按下

            Assert.False(observer.OnKey(VK_LSHIFT, isDown: true, now: 11_100)); // 触发自愈 + Shift down
            Assert.True(observer.OnKey(VK_LSHIFT, isDown: false, now: 11_200));
        }

        [Fact]
        public void CurrentModifierDown_IsNotClearedByItsOwnSync()
        {
            // Alt 仍物理按着；Shift 刚按下（钩子回调时系统状态表里 Shift 还是抬起）。
            // 自愈发生在喂入 Shift 之前，因此不会把 Shift 自己清掉。
            var state = new PhysicalState { Alt = true, Shift = false };
            var observer = new ImeKeyObserver(new ImeSwitchChordTracker(), state.Read);

            Assert.False(observer.OnKey(VK_LMENU, isDown: true, now: 10_000)); // Alt down

            Assert.False(observer.OnKey(VK_LSHIFT, isDown: true, now: 11_100)); // 自愈 + Shift down
            Assert.True(observer.OnKey(VK_LSHIFT, isDown: false, now: 11_200));
        }

        [Fact]
        public void KeyUp_IsHandledBeforeAnySync()
        {
            // keyup 绝不能先自愈：否则会先清掉「独立修饰键点击」的状态，提示丢失。
            var state = new PhysicalState { Shift = true };
            var observer = new ImeKeyObserver(new ImeSwitchChordTracker(), state.Read);

            Assert.False(observer.OnKey(VK_LSHIFT, isDown: true, now: 10_000));

            state.Shift = false;

            // 距离上次已超过自愈间隔，但 keyup 路径不做自愈。
            Assert.True(observer.OnKey(VK_LSHIFT, isDown: false, now: 11_100));
            Assert.Equal(0, state.Reads);
        }

        [Fact]
        public void ModifierSync_IsThrottled()
        {
            var state = new PhysicalState { Ctrl = true };
            var observer = new ImeKeyObserver(new ImeSwitchChordTracker(), state.Read);

            Assert.False(observer.OnKey(0x11, isDown: true, now: 10_000)); // Ctrl down

            // 第一次非修饰键按下触发一次自愈。
            Assert.False(observer.OnKey(VK_A, isDown: true, now: 10_100));
            Assert.Equal(1, state.Reads);

            // 间隔不足：不再读取物理状态。
            Assert.False(observer.OnKey(VK_B, isDown: true, now: 10_500));
            Assert.Equal(1, state.Reads);

            // 超过间隔：再次自愈。
            Assert.False(observer.OnKey(VK_A, isDown: true, now: 11_200));
            Assert.Equal(2, state.Reads);
        }

        [Fact]
        public void CtrlSpace_TriggersOnKeyDown_AndSyncKeepsHeldCtrl()
        {
            // 用户实测的常见松键顺序：Space 按下后先松 Ctrl、再松 Space。
            // 触发发生在 Space 按下时，因此与松键顺序无关；
            // 此刻触发的低频自愈必须读到 Ctrl 仍按住，不能把本次组合清掉。
            var state = new PhysicalState();
            var observer = new ImeKeyObserver(new ImeSwitchChordTracker(), state.Read);

            Assert.False(observer.OnKey(VK_CONTROL, isDown: true, now: 10_000)); // 无追踪状态，先不自愈

            state.Ctrl = true; // 钩子先于系统更新状态表，但 Space 按下时 Ctrl 已在表里

            Assert.True(observer.OnKey(VK_SPACE, isDown: true, now: 10_050));    // 自愈 + 触发
            Assert.Equal(1, state.Reads);

            Assert.False(observer.OnKey(VK_CONTROL, isDown: false, now: 10_120)); // Ctrl 先松
            Assert.False(observer.OnKey(VK_SPACE, isDown: false, now: 10_150));   // Space 后松，不重复
        }

        [Fact]
        public void StaleCtrl_IsHealedBeforeSpace_NoFalseTrigger()
        {
            // Ctrl 的 keyup 丢失、物理已松开：空格按下前的自愈必须先清掉陈旧 Ctrl，
            // 避免把普通空格误判成 Ctrl+Space 切换。
            var state = new PhysicalState { Ctrl = true };
            var observer = new ImeKeyObserver(new ImeSwitchChordTracker(), state.Read);

            Assert.False(observer.OnKey(VK_CONTROL, isDown: true, now: 10_000)); // 无追踪状态，先不自愈

            state.Ctrl = false; // keyup 丢失，物理已松开

            Assert.False(observer.OnKey(VK_SPACE, isDown: true, now: 11_100)); // 自愈清掉陈旧 Ctrl + 空格按下
            Assert.Equal(1, state.Reads);
            Assert.False(observer.OnKey(VK_SPACE, isDown: false, now: 11_200));
        }

        [Fact]
        public void Reset_ClearsChordState()
        {
            var state = new PhysicalState();
            var observer = new ImeKeyObserver(new ImeSwitchChordTracker(), state.Read);

            Assert.False(observer.OnKey(VK_SHIFT, isDown: true, now: 10_000));
            observer.Reset();

            // 状态已清：缺失 keydown 的 keyup 不得凭空触发。
            Assert.False(observer.OnKey(VK_SHIFT, isDown: false, now: 10_100));
        }
    }
}

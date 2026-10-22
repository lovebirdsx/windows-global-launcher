using System;

namespace CommandLauncher
{
    /// <summary>物理修饰键状态（由调用方经 GetAsyncKeyState 读取）。</summary>
    internal readonly record struct ImeModifierState(bool Shift, bool Ctrl, bool Alt, bool Win);

    /// <summary>
    /// 键盘观察的纯编排层：把每次按下/松开原样喂给 <see cref="ImeSwitchChordTracker"/>，
    /// 并负责低频的修饰键物理状态自愈。
    /// <para>
    /// 自愈顺序是硬约束：低级键盘钩子的回调先于系统按键状态表更新，若先喂入当前事件
    /// 再读 GetAsyncKeyState，刚按下的修饰键会被读成「已松开」并被自愈逻辑清掉。
    /// 因此只在 keydown 时、且在喂入当前事件<b>之前</b>做自愈；keyup 绝不自愈，
    /// 否则会先把「独立修饰键点击」的状态清掉，导致切换手势提示丢失。
    /// </para>
    /// </summary>
    internal sealed class ImeKeyObserver
    {
        /// <summary>修饰键物理状态自愈的最小间隔（覆盖 keyup 丢失的场景）。</summary>
        public const int ModifierSyncIntervalMs = 1000;

        private readonly ImeSwitchChordTracker _tracker;
        private readonly Func<ImeModifierState> _physicalState;
        private long _lastSyncTick;

        public ImeKeyObserver(ImeSwitchChordTracker tracker, Func<ImeModifierState> physicalState)
        {
            _tracker = tracker;
            _physicalState = physicalState;
        }

        /// <summary>
        /// 喂入一次按键事件。返回 true 表示应触发一次输入法状态检查
        /// （独立的修饰键点击，或 Ctrl/Win+Space 的空格松开）。
        /// </summary>
        public bool OnKey(int vk, bool isDown, long now)
        {
            if (isDown)
            {
                if (_tracker.HasTrackedState &&
                    now - _lastSyncTick >= ModifierSyncIntervalMs)
                {
                    _lastSyncTick = now;
                    ImeModifierState state = _physicalState();
                    _tracker.Synchronize(state.Shift, state.Ctrl, state.Alt, state.Win);
                }

                _tracker.OnKeyDown(vk);
                return false;
            }

            return _tracker.OnKeyUp(vk);
        }

        /// <summary>清空组合键追踪与自愈计时（录制开始/结束、禁用、钩子重装等场景）。</summary>
        public void Reset()
        {
            _tracker.Reset();
            _lastSyncTick = 0;
        }
    }
}

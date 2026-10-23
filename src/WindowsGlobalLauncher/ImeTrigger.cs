using System.Collections.Generic;

namespace CommandLauncher
{
    /// <summary>
    /// 输入法切换组合键追踪器（纯内存、无分配、无日志）。
    ///
    /// 目的：Shift 单独按下再松开是 Windows 默认的「切换中英文」手势，
    /// 但 Shift+字母、Shift+方向键、Alt+Tab 等组合不应误触发提示。
    /// 规则：修饰键按下后若又按下了别的键（或 Ctrl/Win+Space），
    /// 视为组合键的一部分，松开修饰键时不再单独提示。
    /// Ctrl/Win+Space 在**空格按下**时触发一次：系统在处理该 keydown 时就完成切换，
    /// 等 keyup 再触发会被松键顺序影响（Ctrl 先松是常见操作，那次切换会整次丢失）。
    ///
    /// 用「按下的 VK 集合」而不是计数：键盘自动重复会对同一个修饰键反复发 keydown，
    /// 若每次 keydown 都累加计数，一次物理松开无法归零，追踪器会永久认为修饰键还按着，
    /// 进而让后续切换手势判定失效（Alt+Tab、Ctrl+滚轮之后必现）。
    /// 左右变体同按仍按两个集合元素计数，松开最后一个才触发。
    /// </summary>
    public sealed class ImeSwitchChordTracker
    {
        private const int VK_SPACE = 0x20;

        // 通用 VK 与左右变体（低层钩子在不同键盘上可能混用）
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;

        private const int FamilyShift = 1;
        private const int FamilyCtrl = 2;
        private const int FamilyAlt = 4;
        private const int FamilyWin = 8;

        /// <summary>当前物理按住的修饰键 VK（已把通用 VK 归一化到左变体）。</summary>
        private readonly HashSet<int> _pressed = new();

        private int _shift;
        private int _ctrl;
        private int _alt;
        private int _win;

        /// <summary>位标记：该修饰键族参与过组合键，松开时不应单独触发提示。</summary>
        private int _used;

        /// <summary>Ctrl/Alt/Win 任一按住（Shift 不影响判断）。</summary>
        public bool AnyCtrlAltWinHeld => _ctrl > 0 || _alt > 0 || _win > 0;

        /// <summary>
        /// 是否还有需要核对的按键状态（有任何修饰键被判定为按住）。
        /// 调用方据此决定是否做低频物理状态自愈。
        /// </summary>
        public bool HasTrackedState => _shift > 0 || _ctrl > 0 || _alt > 0 || _win > 0;

        /// <summary>
        /// 按键按下。修饰键只把 VK 放入集合（自动重复是幂等的），返回 false；
        /// Ctrl/Win+Space 在空格按下时返回 true，表示应立即触发一次提示。
        /// </summary>
        public bool OnKeyDown(int vk)
        {
            int family = GetModifierFamily(vk);
            if (family != 0)
            {
                // 同一个 VK 的自动重复不再累加：一次物理松开只 Release 一次。
                if (_pressed.Add(NormalizeModifierVk(vk)))
                    Hold(family);
                return false;
            }

            if (vk == VK_SPACE && (_ctrl > 0 || _win > 0))
            {
                // Ctrl+Space / Win+Space 是系统级输入法切换：系统处理这次 keydown 时
                // 就完成了切换，因此按下即触发，与用户先松 Ctrl 还是先松 Space 无关
                //（实测约一半的切换是先松 Ctrl，等 keyup 会整次丢弃）。
                // 按住的所有修饰键都标记为「已参与组合」：松开 Ctrl/Win 不应重复触发。
                // 自动重复的 keydown 同样返回 true：调用方会 Stop/Start 延迟计时器，
                // 等于按住空格期间不断推迟，松手后再查一次。
                MarkHeldUsed(FamilyShift | FamilyCtrl | FamilyAlt | FamilyWin);
                return true;
            }

            // 其余任何非修饰键（字母、方向键、Tab、Enter、F1…）都说明按住的修饰键
            // 属于组合键，松开时不再单独提示。Shift+Space 也走这里。
            MarkHeldUsed(FamilyShift | FamilyCtrl | FamilyAlt | FamilyWin);
            return false;
        }

        /// <summary>
        /// 按键松开。返回 true 表示应触发一次输入法状态提示：独立的修饰键点击（Shift/Ctrl/Alt）。
        /// 空格 keyup 恒返回 false（Space 从不进入按住状态，Ctrl/Win+Space 已在按下时触发）。
        /// </summary>
        public bool OnKeyUp(int vk)
        {
            if (vk == VK_SPACE)
            {
                return false;
            }

            int family = GetModifierFamily(vk);
            if (family == 0)
            {
                return false;
            }

            // 缺失 keydown 的 keyup 不得凭空触发（钩子刚装上时用户可能正按着键）。
            if (!RemovePressedModifier(vk, family))
            {
                return false;
            }

            if (!Release(family))
            {
                // 同族的另一个左右变体还按着（如左右 Shift 同时按住），等最后一个松开
                return false;
            }

            bool used = (_used & family) != 0;
            _used &= ~family;

            // Win 单独按不提示（Win+Space 已在空格按下时触发）
            if (family == FamilyWin)
            {
                return false;
            }

            return !used;
        }

        /// <summary>
        /// 用物理按键状态自愈（调用方经 GetAsyncKeyState 查询）。
        /// 覆盖 keyup 丢失的路径：快捷键录制吞键、安全桌面、钩子被系统超时摘除后重装。
        /// 只有整个修饰键族的物理键都松开时才清除，避免把真实按住的键误清。
        /// </summary>
        public void Synchronize(bool shiftDown, bool ctrlDown, bool altDown, bool winDown)
        {
            if (_shift > 0 && !shiftDown) ClearFamily(FamilyShift);
            if (_ctrl > 0 && !ctrlDown) ClearFamily(FamilyCtrl);
            if (_alt > 0 && !altDown) ClearFamily(FamilyAlt);
            if (_win > 0 && !winDown) ClearFamily(FamilyWin);
        }

        /// <summary>清空全部状态（录制开始/结束、禁用、钩子重装等场景）。</summary>
        public void Reset()
        {
            _pressed.Clear();
            _shift = 0;
            _ctrl = 0;
            _alt = 0;
            _win = 0;
            _used = 0;
        }

        /// <summary>虚拟键码 → 修饰键族（非修饰键返回 0）。</summary>
        private static int GetModifierFamily(int vk)
        {
            switch (vk)
            {
                case VK_SHIFT:
                case VK_LSHIFT:
                case VK_RSHIFT:
                    return FamilyShift;
                case VK_CONTROL:
                case VK_LCONTROL:
                case VK_RCONTROL:
                    return FamilyCtrl;
                case VK_MENU:
                case VK_LMENU:
                case VK_RMENU:
                    return FamilyAlt;
                case 0x5B:
                case 0x5C:
                    return FamilyWin;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// 通用 VK 归一化到左变体：同一物理键的自动重复若混用 0x10/0xA0，
        /// 归一后仍是同一个集合元素，不会重复计数。
        /// </summary>
        private static int NormalizeModifierVk(int vk)
        {
            switch (vk)
            {
                case VK_SHIFT: return VK_LSHIFT;
                case VK_CONTROL: return VK_LCONTROL;
                case VK_MENU: return VK_LMENU;
                default: return vk;
            }
        }

        /// <summary>
        /// 从集合中移除一个修饰键。通用 VK 与左右变体混用时，只有该族仅剩一个候选才代为移除；
        /// 一个都没有（缺失 keydown）或无法唯一确定（左右同按但收到通用 keyup）时不猜。
        /// </summary>
        private bool RemovePressedModifier(int vk, int family)
        {
            if (_pressed.Remove(NormalizeModifierVk(vk)))
                return true;

            int only = 0;
            int count = 0;
            foreach (int pressed in _pressed)
            {
                if (GetModifierFamily(pressed) != family)
                    continue;

                only = pressed;
                if (++count > 1)
                    break;
            }

            if (count != 1)
                return false;

            _pressed.Remove(only);
            return true;
        }

        /// <summary>清掉某个族的全部按键标记（物理状态自愈用）。</summary>
        private void ClearFamily(int family)
        {
            _pressed.RemoveWhere(vk => GetModifierFamily(vk) == family);

            switch (family)
            {
                case FamilyShift: _shift = 0; break;
                case FamilyCtrl: _ctrl = 0; break;
                case FamilyAlt: _alt = 0; break;
                case FamilyWin: _win = 0; break;
            }

            _used &= ~family;
        }

        private void Hold(int family)
        {
            switch (family)
            {
                case FamilyShift: _shift++; break;
                case FamilyCtrl: _ctrl++; break;
                case FamilyAlt: _alt++; break;
                case FamilyWin: _win++; break;
            }
        }

        /// <summary>计数减一，返回该族是否已全部松开（计数归零）。</summary>
        private bool Release(int family)
        {
            switch (family)
            {
                case FamilyShift:
                    if (_shift > 0) _shift--;
                    return _shift == 0;
                case FamilyCtrl:
                    if (_ctrl > 0) _ctrl--;
                    return _ctrl == 0;
                case FamilyAlt:
                    if (_alt > 0) _alt--;
                    return _alt == 0;
                case FamilyWin:
                    if (_win > 0) _win--;
                    return _win == 0;
                default:
                    return true;
            }
        }

        /// <summary>把当前确实按住的修饰键族标记为 used。</summary>
        private void MarkHeldUsed(int families)
        {
            if ((families & FamilyShift) != 0 && _shift > 0) _used |= FamilyShift;
            if ((families & FamilyCtrl) != 0 && _ctrl > 0) _used |= FamilyCtrl;
            if ((families & FamilyAlt) != 0 && _alt > 0) _used |= FamilyAlt;
            if ((families & FamilyWin) != 0 && _win > 0) _used |= FamilyWin;
        }
    }
}

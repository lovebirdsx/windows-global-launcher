using System;
using System.Reflection;
using System.Windows.Threading;
using Xunit;

namespace WindowsGlobalLauncher.Tests;

/// <summary>
/// 回归测试：复现并锁定「剪贴板窗口按 Delete 删除不生效」的缺陷（2026-10-11 修复）。
/// 根因：ClipboardWindow 的 HistoryChanged 订阅曾写 <c>Dispatcher.Invoke(RefreshList)</c>，
/// 而 RefreshList 带可选参数（<c>bool resetSelection = false</c>）——方法组会被编译器合成为
/// Action&lt;bool&gt; 委托，Dispatcher 却按 0 参数调用，DynamicInvoke 抛
/// TargetParameterCountException；每次历史变化（Delete 删除、去重置顶）都走这条路，
/// 数据层删除成功但 UI 刷新从未执行，表现为「按 Delete 后列表纹丝不动」。
/// 修复：显式 lambda <c>Dispatcher.Invoke(() =&gt; RefreshList())</c>（0 参数语义明确）。
/// 本类用与 RefreshList 同签名的桩方法锁定这两种调用方式的运行时差异。
/// </summary>
public class ClipboardWindowTests
{
    /// <summary>与 ClipboardWindow.RefreshList 相同签名（含可选参数）的桩方法。</summary>
    private static void RefreshListStub(bool resetSelection = false)
    {
    }

    private static bool _noArgStubCalled;

    /// <summary>无参桩方法（对应 PinWindow.CloseSelected、UpdateChecker.MarkChecked 等现有调用点）。</summary>
    private static void NoArgStub()
    {
        _noArgStubCalled = true;
    }

    [Fact]
    public void MethodGroupWithOptionalParameter_ViaDispatcherInvoke_Throws()
    {
        // 复现旧写法：带可选参数的方法组直接传给 Dispatcher.Invoke——
        // 合成委托为 Action<bool>，0 参数调用时抛 TargetParameterCountException
        var dispatcher = Dispatcher.CurrentDispatcher;
        Assert.Throws<TargetParameterCountException>(() => dispatcher.Invoke(RefreshListStub));
    }

    [Fact]
    public void ZeroArgMethodGroup_ViaDispatcherInvoke_Executes()
    {
        // 无参方法组（如 PinWindow.CloseSelected）：自然委托类型为 Action，0 参数调用匹配、安全，
        // 全仓其余方法组调用点均属此类、无需改动
        var dispatcher = Dispatcher.CurrentDispatcher;
        _noArgStubCalled = false;
        dispatcher.Invoke(NoArgStub);
        Assert.True(_noArgStubCalled);
    }

    [Fact]
    public void ZeroArgLambda_ViaDispatcherInvoke_Executes()
    {
        // 验证修复写法：显式 lambda（0 参数）经 Dispatcher 正常执行
        var dispatcher = Dispatcher.CurrentDispatcher;
        bool called = false;
        dispatcher.Invoke(() => called = true);
        Assert.True(called);
    }
}

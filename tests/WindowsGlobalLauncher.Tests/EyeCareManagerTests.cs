using CommandLauncher;
using System.Linq;
using Xunit;

namespace WindowsGlobalLauncher.Tests;

public class EyeCareManagerTests
{
    [Fact]
    public void Modes_ContainsAllExpectedModes()
    {
        var names = EyeCareManager.Modes.Select(m => m.Name).ToList();
        Assert.Equal(["正常", "办公"], names);
    }

    [Fact]
    public void CommandName_HasPrefix()
    {
        foreach (var mode in EyeCareManager.Modes)
        {
            Assert.StartsWith(EyeCareManager.CommandPrefix, mode.CommandName);
        }
    }

    [Fact]
    public void FindByCommandName_FindsMode()
    {
        var mode = EyeCareManager.FindByCommandName("护眼：办公");
        Assert.NotNull(mode);
        Assert.Equal("办公", mode.Name);
        Assert.Equal(5500, mode.ColorTempK);
        Assert.Equal(0.85, mode.Brightness);
    }

    [Fact]
    public void FindByCommandName_ReturnsNullForOtherCommands()
    {
        Assert.Null(EyeCareManager.FindByCommandName("config"));
        Assert.Null(EyeCareManager.FindByCommandName("护眼：不存在"));
    }

    [Fact]
    public void KelvinToRgb_6500K_IsIdentity()
    {
        var (r, g, b) = EyeCareManager.KelvinToRgb(6500);
        Assert.Equal(1.0, r, 2);
        Assert.Equal(1.0, g, 2);
        Assert.Equal(1.0, b, 2);
    }

    [Fact]
    public void KelvinToRgb_LowerTemp_ReducesBlue()
    {
        // 低色温应显著降低蓝色增益、基本保留红色
        var (r, g, b) = EyeCareManager.KelvinToRgb(3700);
        Assert.True(b < 0.65, $"蓝色增益应明显降低，实际 {b}");
        Assert.True(r > 0.9, $"红色增益应接近 1，实际 {r}");
        Assert.True(r > g && g > b, "应为 R > G > B 的暖色分布");
    }

    [Fact]
    public void BuildMatrix_NormalMode_IsIdentity()
    {
        var mode = EyeCareManager.Modes.First(m => m.Name == "正常");
        var matrix = EyeCareManager.BuildMatrix(mode);
        Assert.Equal(EyeCareManager.BuildIdentity(), matrix);
    }

    [Fact]
    public void BuildMatrix_ColorFilter_ScalesDiagonal()
    {
        var mode = new EyeCareMode { Name = "测试", Description = "", ColorTempK = 5000, Brightness = 0.9 };
        var matrix = EyeCareManager.BuildMatrix(mode);
        // 只对角线有值，无平移
        Assert.Equal(0f, matrix[4]);
        Assert.Equal(1f, matrix[18]); // Alpha 通道保持恒等
        Assert.True(matrix[12] < matrix[6], "暖色温下蓝色增益应低于绿色");
        Assert.True(matrix[0] >= 0.9f, "红色增益接近亮度系数");
    }
}

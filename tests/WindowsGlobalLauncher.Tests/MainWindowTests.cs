using CommandLauncher;
using System.Globalization;
using Xunit;

namespace WindowsGlobalLauncher.Tests;

public class MainWindowTests
{
    [Theory]
    [InlineData("clear", "", "clear")]
    [InlineData("clear", "Ctrl+Shift+A", "clear (⌃⇧A)")]
    [InlineData("regedit", "Ctrl+Shift+R", "regedit (⌃⇧R)")]
    [InlineData("notepad", "Alt+N", "notepad (⌥N)")]
    [InlineData("calculator", "Win+C", "calculator (⌘C)")]
    [InlineData("task manager", "Ctrl+Alt+Del", "task manager (⌃⌥DEL)")]
    public void CommandNameWithHotKeyConverter_FormatsCorrectly(string name, string hotKey, string expected)
    {
        var converter = new CommandNameWithHotKeyConverter();
        var result = converter.Convert([name, hotKey], typeof(string), string.Empty, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }
}

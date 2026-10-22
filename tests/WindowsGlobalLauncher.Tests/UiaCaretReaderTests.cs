using System.Linq;
using System.Reflection;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// UIA 读取层的纯决策与 ComImport 声明形状测试。
    /// 不访问真实 UIA/Win32/provider，不宣称任何运行时 provider 验收通过。
    /// </summary>
    public class UiaCaretReaderTests
    {
        [Fact]
        public void TextPattern2_DeclaresFullFlattenedVtable_WithCaretRangeLast()
        {
            // 只验证 ComImport 接口的声明形状：必须平铺 6 个基槽 + 2 个新槽，
            // GetCaretRange 是第 8 个（最后）槽位。若以后改回 C# 接口继承并只声明
            // 两个新方法，这个测试会失败，防止 vtable 槽位再次错位。
            var methods = typeof(UiaCaretReader.IUIAutomationTextPattern2)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(m => m.MetadataToken)
                .Select(m => m.Name)
                .ToArray();

            Assert.Equal(8, methods.Length);
            Assert.Equal("GetCaretRange", methods[^1]);
            Assert.True(
                new[]
                {
                    "RangeFromPoint",
                    "RangeFromChild",
                    "GetSelection",
                    "GetVisibleRanges",
                    "GetDocumentRange",
                    "GetSupportedTextSelection",
                    "RangeFromAnnotation",
                    "GetCaretRange",
                }.SequenceEqual(methods),
                $"实际槽位顺序: {string.Join(", ", methods)}");
        }

        [Theory]
        [InlineData(true, false, true, true, null, false, null, false, true)]   // Edit 正常
        [InlineData(true, false, true, true, true, false, null, false, false)]  // ValuePattern 只读
        [InlineData(true, false, true, true, null, false, true, false, false)]  // TextPattern 只读
        [InlineData(true, false, false, true, null, false, null, false, false)] // 无焦点
        [InlineData(true, false, true, false, null, false, null, false, false)] // 禁用
        [InlineData(false, true, true, true, null, false, null, false, false)]  // Document 无 TextPattern
        [InlineData(false, true, true, true, null, true, true, false, false)]   // Document 只读
        [InlineData(false, true, true, true, null, true, false, false, true)]   // Document 明确非只读
        [InlineData(false, true, true, true, null, true, null, true, true)]     // Document 有 active caret
        [InlineData(false, true, true, true, null, true, null, false, false)]   // Document 无 caret/未证明
        [InlineData(false, true, true, true, false, false, null, false, true)]  // Document Value=false 独立正向证据（无 TextPattern）
        [InlineData(false, true, true, true, true, true, false, false, false)]  // Document Value=true 否决文本 false
        [InlineData(false, true, true, true, false, true, true, false, false)]  // Document Text=true 否决 Value=false
        [InlineData(false, true, false, true, false, false, null, false, false)] // Document 无焦点时即使 Value=false 也不放行
        [InlineData(false, false, true, true, null, false, null, false, false)] // 按钮/菜单等
        public void DecideEditable_RequiresEditableEvidenceAndHonorsReadOnly(
            bool isEdit, bool isDocument, bool focusState, bool enabled,
            bool? valueReadOnly, bool textPatternAvailable, bool? textReadOnly,
            bool caretActive, bool expected)
        {
            Assert.Equal(expected, UiaCaretReader.DecideEditable(
                isEdit, isDocument, focusState, enabled,
                valueReadOnly, textPatternAvailable, textReadOnly, caretActive));
        }

        /// <summary>
        /// 候选类型不是可编辑证据：ComboBox 只认 ValuePattern=false；
        /// Custom 只认 Value=false 或「文本模式可用且明确可写」；
        /// 两者都要求真实焦点、启用、非密码与非空 RuntimeId。
        /// </summary>
        [Theory]
        // ComboBox
        [InlineData(50003, true, false, true, false, false, false, null, true, true)]
        [InlineData(50003, true, false, true, false, false, true, false, true, true)]   // 文本 false 不否决 Value=false
        [InlineData(50003, true, false, true, false, null, true, false, true, false)]   // 仅 Text=false 不够
        [InlineData(50003, true, false, true, false, null, false, null, true, false)]   // Value 未知
        [InlineData(50003, true, false, true, false, true, true, false, true, false)]   // Value=true 否决
        [InlineData(50003, true, false, true, false, false, true, true, true, false)]   // Text=true 否决
        [InlineData(50003, false, true, true, false, false, false, null, true, false)]  // 仅 focusable
        [InlineData(50003, true, false, true, false, false, false, null, false, false)] // 缺身份
        [InlineData(50003, true, false, true, true, false, false, null, true, false)]   // 密码
        [InlineData(50003, true, false, false, false, false, false, null, true, false)] // 禁用
        // Custom
        [InlineData(50026, true, false, true, false, false, false, null, true, true)]   // Value=false
        [InlineData(50026, true, false, true, false, null, true, false, true, true)]    // Text=false 且模式可用
        [InlineData(50026, true, false, true, false, null, false, false, true, false)]  // Text=false 但模式不可用
        [InlineData(50026, true, false, true, false, null, true, null, true, false)]    // 两者未知
        [InlineData(50026, true, false, true, false, null, true, true, true, false)]    // Text=true
        [InlineData(50026, true, false, true, false, true, true, false, true, false)]   // Value=true
        [InlineData(50026, true, false, true, false, false, true, true, true, false)]   // Text=true 否决 Value=false
        [InlineData(50026, false, true, true, false, false, false, null, true, false)]  // 仅 focusable
        [InlineData(50026, true, false, true, false, false, false, null, false, false)] // 缺身份
        [InlineData(50026, true, false, true, true, false, false, null, true, false)]   // 密码
        [InlineData(50026, true, false, false, false, false, false, null, true, false)] // 禁用
        public void DecideProviderEditable_NewCandidatesRequireStrictPositiveEvidence(
            int controlType, bool hasKeyboardFocus, bool focusable, bool enabled, bool isPassword,
            bool? valueReadOnly, bool textPatternAvailable, bool? textReadOnly,
            bool hasRuntimeId, bool expected)
        {
            Assert.Equal(expected, UiaCaretReader.DecideProviderEditable(
                controlType,
                terminalProvider: false,
                hasKeyboardFocus,
                focusable,
                enabled,
                isPassword,
                valueReadOnly,
                textPatternAvailable,
                textReadOnly,
                caretActive: false,
                hasRuntimeId: hasRuntimeId));
        }
    }
}

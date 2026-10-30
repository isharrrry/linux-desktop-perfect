// T2 · 系统兜底族出口验收：确定性 / 有原则 / 空集明确失败。
using System;
using System.IO;
using Xunit;
using Xunit.Abstractions;
using MS.Internal.Text.TextInterface.Linux;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    public class DefaultFamilyTests
    {
        public DefaultFamilyTests(ITestOutputHelper output) => Output = output;
        private ITestOutputHelper Output { get; }

        private const string SystemFontDir = "/usr/share/fonts";

        private static LinuxFontCollection FontsOnly() =>
            LinuxFontCollection.FromDirectory(TestLayout.FontDir, recurse: true, stripLayout: false);

        private static LinuxFontCollection FontsPlusSystem()
        {
            if (!Directory.Exists(SystemFontDir)) return null;
            return LinuxFontDirectories();
        }

        private static LinuxFontCollection LinuxFontDirectories() =>
            LinuxFontCollection.FromDirectories(new[] { TestLayout.FontDir, SystemFontDir },
                                                 recurse: true, stripLayout: false);

        [Fact]
        public void SelectsNotoForFontsOnly_ByRuleNotByScanOrder()
        {
            using LinuxFontCollection c = FontsOnly();
            DefaultFamilyChoice choice = DefaultFontFamily.Select(c);
            Output.WriteLine("fonts-only -> " + choice.FamilyName + " / " + choice.Reason + " :: " + choice.Detail);
            Assert.Equal("Noto Sans", choice.FamilyName);
            Assert.Contains(choice.Reason, new[] { "fontconfig-sans-serif", "preferred-list", "name-order" });
        }

        [Fact]
        public void SameFamilyForFontsOnlyAndFontsPlusSystem()
        {
            using LinuxFontCollection only = FontsOnly();
            using LinuxFontCollection withSystem = FontsPlusSystem();
            if (withSystem == null) { Output.WriteLine("SKIP: 本机没有 " + SystemFontDir); return; }

            DefaultFamilyChoice a = DefaultFontFamily.Select(only);
            DefaultFamilyChoice b = DefaultFontFamily.Select(withSystem);
            Output.WriteLine("fonts-only   -> " + a.FamilyName + " / " + a.Reason + " (families=" + only.FamilyCount + ")");
            Output.WriteLine("fonts+system -> " + b.FamilyName + " / " + b.Reason + " (families=" + withSystem.FamilyCount + ")");

            // 关键断言：加上系统目录**不改变**兜底族（T1 的病灶：之前会变成 AR PL UKai CN）
            Assert.Equal(a.FamilyName, b.FamilyName);
        }

        [Fact]
        public void DeterministicAcrossRepeatedBuilds()
        {
            string first = null;
            for (int i = 0; i < 5; i++)
            {
                using LinuxFontCollection c = FontsOnly();
                string name = DefaultFontFamily.Select(c).FamilyName;
                first ??= name;
                Assert.Equal(first, name);
            }
        }

        [Fact]
        public void EmptyCollectionFailsLoudly()
        {
            string missing = Path.Combine(Path.GetTempPath(), "no-such-font-dir-" + Guid.NewGuid().ToString("N"));
            using LinuxFontCollection empty = LinuxFontCollection.FromDirectory(missing, recurse: true, stripLayout: false);
            Assert.Equal(0, empty.FamilyCount);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => DefaultFontFamily.Select(empty));
            Output.WriteLine("空集报文：" + ex.Message);
            Assert.Contains("字体集为空", ex.Message);
            Assert.Contains("src/Linux/build/fonts", ex.Message);
            Assert.Contains("WPF_LINUX_TEXT_FONT_DIR", ex.Message);
        }
    }
}

using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.SmartFinds;

namespace DiskSweeper.Core.Tests;

public class ReleaseSafetyTests
{
    [Theory]
    [InlineData("powercfg /h off", true)]
    [InlineData("DISM /Online /Cleanup-Image /StartComponentCleanup", true)]
    [InlineData("SystemPropertiesPerformance.exe", true)]
    [InlineData("SystemPropertiesProtection.exe", true)]
    [InlineData("start ms-settings:storagesense", true)]
    [InlineData("start https://github.com/Klocman/Bulk-Crap-Uninstaller/releases", true)]
    [InlineData("start C:\\Windows\\System32\\cmd.exe", false)]
    [InlineData("cmd.exe /c calc", false)]
    [InlineData("..\\evil.exe", false)]
    [InlineData("C:\\Windows\\System32\\calc.exe", false)]
    [InlineData("powercfg /h off; Remove-Item C:\\", false)]
    [InlineData("$(calc)", false)]
    [InlineData("powercfg /h off\nRemove-Item C:\\", false)]
    public void Advice_commands_only_allow_the_shipped_shapes(string command, bool allowed)
    {
        Assert.Equal(allowed, AdviceCommands.IsAllowed(command));
    }

    [Fact]
    public void Every_built_in_advice_command_is_allowed()
    {
        foreach (var rule in JunkRule.LoadBuiltIn())
        {
            if (rule.AdviceCommand is null) continue;
            Assert.True(AdviceCommands.IsAllowed(rule.AdviceCommand), rule.Id + ": " + rule.AdviceCommand);
        }
    }

    [Fact]
    public void Elevated_results_can_only_be_written_to_a_temp_file_we_named()
    {
        string good = Path.Join(Path.GetTempPath(), $"DiskSweeper-{Guid.NewGuid():N}.json");
        Assert.True(AdminJob.IsAcceptableResultFile(good));

        AdminJob.WriteResults(good, []);
        try
        {
            Assert.Equal("[]", File.ReadAllText(good));
        }
        finally
        {
            if (File.Exists(good)) File.Delete(good);
        }

        Assert.False(AdminJob.IsAcceptableResultFile(Path.Join(Path.GetTempPath(), "notes.json")));
        Assert.False(AdminJob.IsAcceptableResultFile(Path.Join(Path.GetTempPath(), "DiskSweeper-not-a-guid.json")));
        Assert.False(AdminJob.IsAcceptableResultFile(Path.Join(Path.GetTempPath(), "nested", $"DiskSweeper-{Guid.NewGuid():N}.json")));
        Assert.False(AdminJob.IsAcceptableResultFile(Path.Join(Path.GetTempPath(), "..", $"DiskSweeper-{Guid.NewGuid():N}.json")));
        Assert.False(AdminJob.IsAcceptableResultFile(good + ":stream"));
        Assert.Throws<ArgumentException>(() => AdminJob.WriteResults(Path.Join(Path.GetTempPath(), "notes.json"), []));
    }

    [Fact]
    public void Deletion_log_neutralizes_spreadsheet_formulas()
    {
        using var box = new Sandbox();
        var log = new DeletionLog(box.PathOf("log.csv"));
        log.Write("Delete", "Deleted", 12, @"=HYPERLINK(""http://evil"")", "note, with comma");
        log.Write("Delete", "Deleted", 12, @"C:\Games\old.iso", "removed");

        string text = File.ReadAllText(log.FilePath);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", text);
        Assert.Contains(@"C:\Games\old.iso", text);
        Assert.DoesNotContain(@"'=C:\Games", text);
    }
}

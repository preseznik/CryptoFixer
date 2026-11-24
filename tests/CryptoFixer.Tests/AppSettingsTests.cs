using CryptoFixer.Core.Settings;

namespace CryptoFixer.Tests;

public class AppSettingsTests
{
    [Theory]
    [InlineData("standard", TableDisplayMode.Standard)]
    [InlineData("current", TableDisplayMode.Standard)]
    [InlineData(" Compact ", TableDisplayMode.Compact)]
    [InlineData("LIST", TableDisplayMode.List)]
    public void Parse_reads_display_mode_case_insensitively(string token, TableDisplayMode expected)
    {
        var result = AppSettingsFile.Parse($$"""
            {
              // comments and trailing commas are fine
              "DisplayMode": "{{token}}",
            }
            """);

        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        Assert.Equal(expected, result.Settings.DisplayMode);
    }

    [Theory]
    [InlineData("""{ "displayMode": "tiles" }""", false)]
    [InlineData("""{ "displayMode": 2 }""", false)]
    [InlineData("""{ "displayMode": """, true)]
    [InlineData("""[ "list" ]""", true)]
    public void Invalid_content_falls_back_to_defaults(string json, bool isError)
    {
        var result = AppSettingsFile.Parse(json);

        Assert.Equal(TableDisplayMode.Standard, result.Settings.DisplayMode);
        Assert.Equal(isError, result.Error is not null);
        Assert.Equal(!isError, result.Warnings.Count == 1);
    }

    [Theory]
    [InlineData("""{ "checkForUpdates": false }""", false, 0)]
    [InlineData("""{ "CHECKFORUPDATES": true }""", true, 0)]
    [InlineData("""{ "displayMode": "list" }""", true, 0)]
    [InlineData("""{ "checkForUpdates": "no" }""", true, 1)]
    public void Parse_reads_the_update_check_switch(string json, bool expected, int warnings)
    {
        var result = AppSettingsFile.Parse(json);

        Assert.Null(result.Error);
        Assert.Equal(warnings, result.Warnings.Count);
        Assert.Equal(expected, result.Settings.CheckForUpdates);
    }

    [Fact]
    public void Save_and_load_round_trip_on_disk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "CryptoFixerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = AppSettingsFile.DefaultPath(folder);
            Assert.Equal(AppSettingsLoadResult.Missing, AppSettingsFile.Load(path));

            AppSettingsFile.Save(path, new AppSettings { DisplayMode = TableDisplayMode.List });
            AppSettingsFile.Save(path, new AppSettings { DisplayMode = TableDisplayMode.Compact, CheckForUpdates = false });

            var loaded = AppSettingsFile.Load(path);
            Assert.True(loaded.FileExists);
            Assert.Null(loaded.Error);
            Assert.Equal(TableDisplayMode.Compact, loaded.Settings.DisplayMode);
            Assert.False(loaded.Settings.CheckForUpdates);
            Assert.Contains("\"displayMode\": \"compact\"", File.ReadAllText(path));
            Assert.Contains("\"checkForUpdates\": false", File.ReadAllText(path));
            Assert.Equal([AppSettingsFile.FileName], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

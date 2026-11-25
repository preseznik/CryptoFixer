using CryptoFixer.Core.Presets;
using CryptoFixer.Core.Schannel;

namespace CryptoFixer.Tests;

public class PresetTests
{
    private const int Server2019 = 17763;
    private const int Server2022 = 20348;

    private static IReadOnlyList<ProtocolRoleState> Read(InMemoryRegistry registry, int build) =>
        new SchannelProtocolStore(registry).ReadAll(build);

    private static ProtocolPreset BuiltIn(IReadOnlyList<ProtocolRoleState> states, string name) =>
        BuiltInPresets.For(states).Single(p => p.Name == name);

    [Fact]
    public void Modern_preset_on_server_2019_disables_legacy_and_leaves_tls13_unconfigured()
    {
        var registry = new InMemoryRegistry();
        registry.SetProtocol(ProtocolCatalog.Tls13, ProtocolRole.Server, 1, 0);
        var states = Read(registry, Server2019);

        var preset = BuiltIn(states, BuiltInPresets.ModernName);
        var changes = PresetPlanner.Plan(preset, states).DifferencesFrom(states);

        ConfiguredSetting? Target(SchannelProtocol p, ProtocolRole r) =>
            changes.SingleOrDefault(c => c.Protocol == p && c.Role == r)?.Target;

        Assert.True(preset.IsBuiltIn);
        Assert.Equal(ConfiguredSetting.Disabled, Target(ProtocolCatalog.Tls10, ProtocolRole.Server));
        Assert.Equal(ConfiguredSetting.Disabled, Target(ProtocolCatalog.Ssl30, ProtocolRole.Client));
        Assert.Equal(ConfiguredSetting.Disabled, Target(ProtocolCatalog.Dtls10, ProtocolRole.Client));
        Assert.Equal(ConfiguredSetting.Enabled, Target(ProtocolCatalog.Tls12, ProtocolRole.Server));
        Assert.Equal(ConfiguredSetting.NotConfigured, Target(ProtocolCatalog.Tls13, ProtocolRole.Server));
        Assert.Null(Target(ProtocolCatalog.Tls13, ProtocolRole.Client)); // already not configured
        Assert.DoesNotContain(ChangeAdvisor.GetWarnings(states, changes), w => w.Contains("No TLS 1.2", StringComparison.Ordinal));
    }

    [Fact]
    public void Modern_preset_leaves_dtls10_alone_when_dtls12_is_not_supported()
    {
        var states = Read(new InMemoryRegistry(), 9600); // Server 2012 R2: no DTLS 1.2

        var preset = BuiltIn(states, BuiltInPresets.ModernName);

        Assert.DoesNotContain(preset.Entries, e => e.Protocol == ProtocolCatalog.Dtls10);
    }

    [Fact]
    public void Modern_preset_has_no_differences_once_applied()
    {
        var store = new SchannelProtocolStore(new InMemoryRegistry());
        var before = store.ReadAll(Server2022);
        store.Apply(PresetPlanner.Plan(BuiltIn(before, BuiltInPresets.ModernName), before).DifferencesFrom(before));

        var after = store.ReadAll(Server2022);
        Assert.Empty(PresetPlanner.Plan(BuiltIn(after, BuiltInPresets.ModernName), after).DifferencesFrom(after));
    }

    [Fact]
    public void Os_defaults_preset_only_changes_configured_roles()
    {
        var registry = new InMemoryRegistry();
        registry.SetProtocol(ProtocolCatalog.Tls12, ProtocolRole.Client, 1, 0);
        registry.SetProtocol(ProtocolCatalog.Ssl20, ProtocolRole.Client, null, 1);
        var states = Read(registry, Server2022);

        var changes = PresetPlanner.Plan(BuiltIn(states, BuiltInPresets.OsDefaultsName), states).DifferencesFrom(states);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(ConfiguredSetting.NotConfigured, c.Target));
    }

    [Fact]
    public void Planner_skips_enabling_protocols_the_os_does_not_support()
    {
        var states = Read(new InMemoryRegistry(), Server2019);
        var preset = new ProtocolPreset("From 2022",
        [
            new PresetEntry(ProtocolCatalog.Tls13, ProtocolRole.Server, ConfiguredSetting.Enabled),
            new PresetEntry(ProtocolCatalog.Tls13, ProtocolRole.Client, ConfiguredSetting.Disabled),
            new PresetEntry(ProtocolCatalog.Tls12, ProtocolRole.Server, ConfiguredSetting.Enabled),
        ]);

        var plan = PresetPlanner.Plan(preset, states);

        Assert.Equal(2, plan.Targets.Count);
        Assert.DoesNotContain(plan.Targets, t => t.Protocol == ProtocolCatalog.Tls13 && t.Role == ProtocolRole.Server);
        Assert.Contains(plan.Targets, t => t.Protocol == ProtocolCatalog.Tls13 && t.Target == ConfiguredSetting.Disabled);
        var note = Assert.Single(plan.Skipped);
        Assert.Contains("TLS 1.3 Server", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_leaves_out_custom_values_and_keeps_metadata()
    {
        var savedAt = new DateTimeOffset(2026, 11, 24, 10, 30, 0, TimeSpan.FromHours(1));
        var preset = ProtocolPreset.Capture(
            "  Web  ",
            "  ",
            [
                new PresetEntry(ProtocolCatalog.Tls12, ProtocolRole.Client, ConfiguredSetting.Enabled),
                new PresetEntry(ProtocolCatalog.Tls11, ProtocolRole.Server, ConfiguredSetting.Custom),
                new PresetEntry(ProtocolCatalog.Tls10, ProtocolRole.Server, ConfiguredSetting.Disabled),
            ],
            savedAt,
            "HOST, Windows Server 2022",
            out var skipped);

        Assert.Equal("Web", preset.Name);
        Assert.Null(preset.Description);
        Assert.Equal(savedAt, preset.SavedAt);
        Assert.Equal(ProtocolCatalog.Tls11, Assert.Single(skipped).Protocol);
        Assert.Equal([ProtocolCatalog.Tls10, ProtocolCatalog.Tls12], preset.Entries.Select(e => e.Protocol));
    }

    [Fact]
    public void File_format_round_trips_and_uses_shorthand_when_both_sides_match()
    {
        var preset = new ProtocolPreset("Web servers",
        [
            new PresetEntry(ProtocolCatalog.Tls10, ProtocolRole.Server, ConfiguredSetting.Disabled),
            new PresetEntry(ProtocolCatalog.Tls10, ProtocolRole.Client, ConfiguredSetting.Disabled),
            new PresetEntry(ProtocolCatalog.Tls13, ProtocolRole.Server, ConfiguredSetting.Enabled),
            new PresetEntry(ProtocolCatalog.Tls13, ProtocolRole.Client, ConfiguredSetting.NotConfigured),
            new PresetEntry(ProtocolCatalog.Ssl30, ProtocolRole.Client, ConfiguredSetting.Disabled),
        ])
        {
            Description = "IIS – ünïcode",
            SavedAt = new DateTimeOffset(2026, 11, 24, 10, 30, 0, TimeSpan.FromHours(1)),
            SavedFrom = "WEB01, Windows Server 2022",
        };
        var builtIn = BuiltInPresets.For(Read(new InMemoryRegistry(), Server2022))[0];

        var json = PresetFile.Serialize([preset, builtIn]);
        var result = PresetFile.Parse(json);

        Assert.Contains("\"TLS 1.0\": \"disabled\"", json, StringComparison.Ordinal);
        Assert.Contains("\"client\": \"osDefault\"", json, StringComparison.Ordinal);
        Assert.Contains("ünïcode", json, StringComparison.Ordinal);
        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        var loaded = Assert.Single(result.Presets); // built-ins are never written
        Assert.Equal(preset.Name, loaded.Name);
        Assert.Equal(preset.Description, loaded.Description);
        Assert.Equal(preset.SavedAt, loaded.SavedAt);
        Assert.Equal(preset.SavedFrom, loaded.SavedFrom);
        Assert.False(loaded.IsBuiltIn);
        Assert.Equal(ProtocolPreset.Normalize(preset.Entries), loaded.Entries);
    }

    [Fact]
    public void Parse_is_lenient_and_reports_what_it_skipped()
    {
        const string json = """
            {
              // hand-edited
              "presets": [
                {
                  "name": "Edited",
                  "protocols": {
                    "tls 1.1": "Default",
                    "TLS 1.2": { "SERVER": "enabled", "both": "enabled" },
                    "TLS 9.9": "disabled",
                    "SSL 3.0": "maybe",
                    "DTLS 1.0": 0,
                  },
                },
                { "protocols": {} },
                { "name": "edited", "protocols": { "TLS 1.0": "disabled" } },
                { "name": "OS defaults: remove all settings", "protocols": {} },
              ],
            }
            """;

        var result = PresetFile.Parse(json);

        Assert.Null(result.Error);
        var preset = Assert.Single(result.Presets);
        Assert.Equal(
            [
                new PresetEntry(ProtocolCatalog.Tls11, ProtocolRole.Server, ConfiguredSetting.NotConfigured),
                new PresetEntry(ProtocolCatalog.Tls11, ProtocolRole.Client, ConfiguredSetting.NotConfigured),
                new PresetEntry(ProtocolCatalog.Tls12, ProtocolRole.Server, ConfiguredSetting.Enabled),
            ],
            preset.Entries);

        string[] expected = ["unknown side \"both\"", "unknown protocol \"TLS 9.9\"", "invalid value \"maybe\"", "DTLS 1.0 must be a value",
            "#2 skipped", "\"edited\" skipped", "built-in preset"];
        foreach (var fragment in expected)
        {
            Assert.Contains(result.Warnings, w => w.Contains(fragment, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("{ \"presets\": [ { \"name\": \"x\" ")]
    [InlineData("[]")]
    [InlineData("{ \"presets\": \"none\" }")]
    public void Parse_reports_an_error_for_unusable_files(string json)
    {
        var result = PresetFile.Parse(json);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Presets);
    }

    [Fact]
    public void Save_load_and_move_aside_work_on_disk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "CryptoFixerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = PresetFile.DefaultPath(folder);
            Assert.False(PresetFile.Load(path).FileExists);

            var preset = new ProtocolPreset("Lab", [new PresetEntry(ProtocolCatalog.Tls10, ProtocolRole.Client, ConfiguredSetting.Disabled)]);
            PresetFile.Save(path, [preset]);
            PresetFile.Save(path, [preset, preset with { Name = "Lab 2" }]);

            var loaded = PresetFile.Load(path);
            Assert.True(loaded.FileExists);
            Assert.Equal(["Lab", "Lab 2"], loaded.Presets.Select(p => p.Name));
            Assert.Equal([PresetFile.FileName], Directory.GetFiles(folder).Select(Path.GetFileName)); // no temp file left behind

            File.WriteAllText(path, "{ broken");
            Assert.NotNull(PresetFile.Load(path).Error);
            var moved = PresetFile.MoveAside(path, new DateTime(2026, 11, 24, 9, 5, 0));
            Assert.EndsWith(".invalid-20261124-090500", moved, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(" modern: tls 1.2 and 1.3 only ", false)]
    [InlineData("Web servers", true)]
    public void ValidateName_rejects_empty_and_built_in_names(string name, bool valid)
    {
        Assert.Equal(valid, PresetFile.ValidateName(name) is null);
    }

    private static IReadOnlyList<AlgorithmState> ReadAlgorithms(InMemoryRegistry registry, int build) =>
        new SchannelProtocolStore(registry).ReadAllAlgorithms(build);

    [Fact]
    public void Algorithm_settings_round_trip_as_format_version_2()
    {
        var preset = new ProtocolPreset("No 3DES", [new PresetEntry(ProtocolCatalog.Tls10, ProtocolRole.Server, ConfiguredSetting.Disabled)])
        {
            Algorithms =
            [
                new AlgorithmPresetEntry(AlgorithmCatalog.TripleDes168, ConfiguredSetting.Disabled),
                new AlgorithmPresetEntry(AlgorithmCatalog.Rc4_128, ConfiguredSetting.Disabled),
                new AlgorithmPresetEntry(AlgorithmCatalog.Aes256, ConfiguredSetting.Enabled),
                new AlgorithmPresetEntry(AlgorithmCatalog.Md5, ConfiguredSetting.NotConfigured),
                new AlgorithmPresetEntry(AlgorithmCatalog.DiffieHellman, ConfiguredSetting.Enabled),
            ],
        };

        var json = PresetFile.Serialize([preset]);
        var result = PresetFile.Parse(json);

        Assert.Contains("\"formatVersion\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"Triple DES 168\": \"disabled\"", json, StringComparison.Ordinal);
        Assert.Contains("\"RC4 128/128\": \"disabled\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hashes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"keyExchange\"", json, StringComparison.Ordinal);
        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        var loaded = Assert.Single(result.Presets);
        Assert.Equal(preset.Entries, loaded.Entries);
        Assert.Equal(ProtocolPreset.Normalize(preset.Algorithms), loaded.Algorithms);
    }

    [Fact]
    public void Protocol_only_presets_are_still_written_and_read_as_format_version_1()
    {
        const string v1 = """
            {
              "formatVersion": 1,
              "presets": [ { "name": "Old", "protocols": { "TLS 1.0": "disabled" } } ]
            }
            """;

        var result = PresetFile.Parse(v1);

        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        var preset = Assert.Single(result.Presets);
        Assert.Empty(preset.Algorithms);
        Assert.Equal(2, preset.Entries.Count);
        Assert.Contains("\"formatVersion\": 1", PresetFile.Serialize([preset]), StringComparison.Ordinal);
    }

    [Fact]
    public void Algorithm_sections_are_lenient_and_report_what_they_skipped()
    {
        const string json = """
            {
              "formatVersion": 2,
              "presets": [
                {
                  "name": "Edited",
                  "ciphers": { "rc4 128/128": "off", " AES 128/128 ": "Enabled", "Blowfish": "disabled", "NULL": "maybe" },
                  "hashes": "none",
                  "keyExchange": { "diffie-hellman": "default" }
                }
              ]
            }
            """;

        var result = PresetFile.Parse(json);

        Assert.Null(result.Error);
        var preset = Assert.Single(result.Presets);
        Assert.Equal(
            [
                new AlgorithmPresetEntry(AlgorithmCatalog.Rc4_128, ConfiguredSetting.Disabled),
                new AlgorithmPresetEntry(AlgorithmCatalog.Aes128, ConfiguredSetting.Enabled),
                new AlgorithmPresetEntry(AlgorithmCatalog.DiffieHellman, ConfiguredSetting.NotConfigured),
            ],
            preset.Algorithms);
        foreach (var fragment in new[] { "unknown cipher \"Blowfish\" skipped", "invalid value \"maybe\"", "\"hashes\" must be an object" })
        {
            Assert.Contains(result.Warnings, w => w.Contains(fragment, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(result.Warnings, w => w.Contains("changes nothing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Server2022)]
    [InlineData(22621)]
    public void Modern_preset_disables_weak_ciphers_keeps_aes_and_leaves_hashes_alone(int build)
    {
        var store = new SchannelProtocolStore(new InMemoryRegistry());
        var states = store.ReadAll(build);
        var algorithms = store.ReadAllAlgorithms(build);
        var preset = BuiltIn(states, BuiltInPresets.ModernName);

        Assert.Equal(AlgorithmCatalog.Ciphers, preset.Algorithms.Select(e => e.Algorithm));
        Assert.All(preset.Algorithms, e =>
            Assert.Equal(e.Algorithm.IsDeprecated ? ConfiguredSetting.Disabled : ConfiguredSetting.Enabled, e.Setting));
        Assert.Equal(["AES 128/128", "AES 256/256"],
            preset.Algorithms.Where(e => e.Setting == ConfiguredSetting.Enabled).Select(e => e.Algorithm.KeyName));

        var plan = PresetPlanner.Plan(preset, states, algorithms);
        var changes = plan.DifferencesFrom(algorithms);
        Assert.Equal(13, changes.Count);
        Assert.Empty(ChangeAdvisor.GetWarnings(algorithms, changes));

        store.Apply(changes);
        var after = store.ReadAllAlgorithms(build);
        Assert.Empty(PresetPlanner.Plan(preset, states, after).DifferencesFrom(after));
        Assert.All(after.Where(s => s.Algorithm.Category == AlgorithmCategory.Cipher), s =>
            Assert.Equal(s.Algorithm.IsDeprecated ? EffectiveState.Disabled : EffectiveState.Enabled, s.Effective));
        Assert.All(after.Where(s => s.Algorithm.Category != AlgorithmCategory.Cipher), s => Assert.False(s.IsExplicitlyConfigured));
    }

    [Fact]
    public void Os_defaults_preset_resets_configured_algorithms_but_keeps_key_sizes()
    {
        var registry = new InMemoryRegistry();
        var dh = SchannelPaths.AlgorithmKey(AlgorithmCatalog.DiffieHellman);
        registry.SetDWord(SchannelPaths.AlgorithmKey(AlgorithmCatalog.TripleDes168), "Enabled", 0);
        registry.SetDWord(SchannelPaths.AlgorithmKey(AlgorithmCatalog.Sha1), "Enabled", 0xFFFFFFFF);
        registry.SetDWord(dh, "ServerMinKeyBitLength", 3072);
        registry.SetDWord(dh, "Enabled", 1);
        var store = new SchannelProtocolStore(registry);
        var states = store.ReadAll(Server2022);
        var algorithms = store.ReadAllAlgorithms(Server2022);

        var preset = BuiltIn(states, BuiltInPresets.OsDefaultsName);
        var changes = PresetPlanner.Plan(preset, states, algorithms).DifferencesFrom(algorithms);

        Assert.Equal(AlgorithmCatalog.All.Count, preset.Algorithms.Count);
        Assert.Equal([AlgorithmCatalog.TripleDes168, AlgorithmCatalog.Sha1, AlgorithmCatalog.DiffieHellman], changes.Select(c => c.Algorithm));
        Assert.All(changes, c => Assert.Equal(ConfiguredSetting.NotConfigured, c.Target));

        store.Apply(changes);
        Assert.All(store.ReadAllAlgorithms(Server2022), s => Assert.False(s.IsExplicitlyConfigured));
        Assert.Equal(3072u, Convert.ToUInt32(registry.GetValue(dh, "ServerMinKeyBitLength")?.Data));
    }

    [Fact]
    public void Capture_with_algorithms_drops_custom_values_and_orders_by_catalog()
    {
        var preset = ProtocolPreset.Capture("Lab", null, [],
            [
                new AlgorithmPresetEntry(AlgorithmCatalog.Aes256, ConfiguredSetting.Enabled),
                new AlgorithmPresetEntry(AlgorithmCatalog.Des56, ConfiguredSetting.Custom),
                new AlgorithmPresetEntry(AlgorithmCatalog.Rc4_40, ConfiguredSetting.Disabled),
            ],
            DateTimeOffset.UnixEpoch, null, out _);

        Assert.Equal([AlgorithmCatalog.Rc4_40, AlgorithmCatalog.Aes256], preset.Algorithms.Select(e => e.Algorithm));
    }

    [Fact]
    public void Planner_ignores_algorithm_entries_without_a_current_state()
    {
        var states = Read(new InMemoryRegistry(), Server2022);
        var preset = BuiltIn(states, BuiltInPresets.ModernName);

        Assert.Empty(PresetPlanner.Plan(preset, states).AlgorithmTargets);
        Assert.Equal(13, PresetPlanner.Plan(preset, states, ReadAlgorithms(new InMemoryRegistry(), Server2022)).AlgorithmTargets.Count);
    }
}

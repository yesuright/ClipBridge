using System.Text.Json;

namespace ClipBridge.Core.Config;

/// <summary>
/// Resolves the effective client configuration for this launch, creating one when needed.
///
/// Contract (portability requirements):
/// - A missing config is provisioned automatically: CSPRNG token, unique device id,
///   unique mDNS name, written atomically. The user never edits JSON to get started.
/// - An existing valid config is never rewritten, never re-tokenized.
/// - A corrupt config is backed up first, then replaced, and the user is told where the copy is.
/// - An unreadable config (permissions/lock) is never overwritten — the app refuses to run
///   rather than destroying a token it cannot see.
/// - A legacy relay .env is imported only inside a real ClipBridge source checkout, so a
///   distributed build can never pick up a developer's token from a nearby folder.
/// </summary>
public static class ConfigBootstrap
{
    public static ConfigBootstrapResult Ensure(AppPathLayout? layout = null)
    {
        layout ??= AppPaths.Current;
        var read = ConfigStore.Read(layout.ConfigFile);
        var notes = new List<string>();
        if (read.Note is not null)
        {
            notes.Add(read.Note);
        }

        // Environment overrides (CLIPBRIDGE_* ) must keep working exactly as before: they
        // are how a second, isolated instance is started for verification and how advanced
        // users point the client at a relay on another machine. They never get persisted.
        var fromEnvironment = ClientConfig.ApplyEnvironment(read.Config).Normalize(out var envNotes);
        notes.AddRange(envNotes);

        switch (read.State)
        {
            case ConfigFileState.Unreadable:
                return new ConfigBootstrapResult(
                    fromEnvironment, layout, layout.ConfigFile, ConfigBootstrapOutcome.Unusable,
                    null, notes, read.Note);

            case ConfigFileState.Missing:
            {
                if (fromEnvironment.HasValidToken)
                {
                    // Everything needed came from the environment: run without writing a file.
                    return new ConfigBootstrapResult(
                        fromEnvironment, layout, layout.ConfigFile, ConfigBootstrapOutcome.Existing,
                        null, notes, null);
                }
                if (ConfigMigration.TryReadLegacyToken(out var legacyToken, out var legacyPath)
                    && !string.IsNullOrWhiteSpace(legacyToken))
                {
                    notes.Add($"Imported the existing relay token from {legacyPath}.");
                    var migrated = read.Config with
                    {
                        DeviceToken = legacyToken!,
                        DeviceId = read.Config.DeviceId,
                    };
                    return Persist(migrated, layout, ConfigBootstrapOutcome.Migrated, null, notes);
                }
                return Provision(read.Config, layout, notes, ConfigBootstrapOutcome.Provisioned,
                    backupPath: null, freshIdentity: true);
            }

            case ConfigFileState.Malformed:
            {
                notes.Add(read.BackupPath is null
                    ? "The previous configuration was unreadable and could not be backed up."
                    : $"The previous configuration was unreadable; a copy was kept at {read.BackupPath}.");
                return Provision(read.Config, layout, notes, ConfigBootstrapOutcome.Recovered,
                    read.BackupPath, freshIdentity: true);
            }

            default:
                if (!fromEnvironment.HasValidToken)
                {
                    notes.Add("The configuration had no token; a new one was generated.");
                    return Provision(read.Config, layout, notes, ConfigBootstrapOutcome.Provisioned,
                        backupPath: null, freshIdentity: false);
                }
                return new ConfigBootstrapResult(
                    fromEnvironment, layout, layout.ConfigFile, ConfigBootstrapOutcome.Existing,
                    null, notes, null);
        }
    }

    private static ConfigBootstrapResult Provision(
        ClientConfig seed,
        AppPathLayout layout,
        List<string> notes,
        ConfigBootstrapOutcome outcome,
        string? backupPath,
        bool freshIdentity)
    {
        var provisioned = seed with
        {
            ConfigVersion = ConfigStore.CurrentSchemaVersion,
            DeviceToken = TokenGenerator.NewToken(),
            DeviceId = freshIdentity || string.IsNullOrWhiteSpace(seed.DeviceId)
                ? TokenGenerator.NewDeviceId()
                : seed.DeviceId,
            // A fresh identity gets its own mDNS name: two strangers running the default
            // "clipbridge" on the same Wi-Fi would fight over one .local name.
            LanHostname = freshIdentity ? NewLanHostname() : seed.LanHostname,
        };
        notes.Add("A new access token was generated on this PC.");
        return Persist(provisioned, layout, outcome, backupPath, notes);
    }

    private static ConfigBootstrapResult Persist(
        ClientConfig config,
        AppPathLayout layout,
        ConfigBootstrapOutcome outcome,
        string? backupPath,
        List<string> notes)
    {
        try
        {
            ConfigStore.Write(config, layout.ConfigFile);
            // The file now holds the durable values; the running process still honours any
            // CLIPBRIDGE_* override on top (and never writes those overrides back).
            var effective = ClientConfig.ApplyEnvironment(config).Normalize(out var envNotes);
            notes.AddRange(envNotes);
            return new ConfigBootstrapResult(
                effective, layout, layout.ConfigFile, outcome, backupPath, notes, null);
        }
        catch (ConfigWriteException ex)
        {
            // The token could not be persisted. Running with it would break the iPhone
            // Shortcut on the next launch, so this is a hard, explained failure.
            notes.Add(ex.Message);
            return new ConfigBootstrapResult(
                config, layout, layout.ConfigFile, ConfigBootstrapOutcome.Unusable,
                backupPath, notes, ex.Message);
        }
    }

    private static string NewLanHostname()
    {
        var machine = new string(Environment.MachineName.ToLowerInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()).Trim('-');
        if (machine.Length == 0)
        {
            return "clipbridge";
        }
        var name = $"clipbridge-{machine}";
        return name.Length <= 63 ? name : name[..63].TrimEnd('-');
    }
}

public enum ConfigBootstrapOutcome
{
    /// <summary>A usable configuration already existed; nothing was written.</summary>
    Existing,

    /// <summary>The file was missing (first run) and has been created.</summary>
    Provisioned,

    /// <summary>The file was corrupt; it was backed up and a new configuration was created.</summary>
    Recovered,

    /// <summary>The token came from a legacy relay .env inside a source checkout.</summary>
    Migrated,

    /// <summary>No usable configuration could be established (unreadable or unwritable).</summary>
    Unusable,
}

public sealed record ConfigBootstrapResult(
    ClientConfig Config,
    AppPathLayout Layout,
    string ConfigPath,
    ConfigBootstrapOutcome Outcome,
    string? BackupPath,
    IReadOnlyList<string> Notes,
    string? Error)
{
    public bool IsUsable => Outcome != ConfigBootstrapOutcome.Unusable && Config.HasValidToken;

    /// <summary>True when this launch created or repaired the config file.</summary>
    public bool WasWritten => Outcome is ConfigBootstrapOutcome.Provisioned
        or ConfigBootstrapOutcome.Recovered
        or ConfigBootstrapOutcome.Migrated;

    /// <summary>Safe one-line summary for the log (no token, no secret).</summary>
    public string Describe()
        => JsonSerializer.Serialize(new
        {
            outcome = Outcome.ToString(),
            config = ConfigPath,
            data = Layout.DataDir,
            device = Config.DeviceId,
            token_fingerprint = TokenGenerator.Fingerprint(Config.DeviceToken),
            notes = Notes.Count,
            error = Error,
        });
}

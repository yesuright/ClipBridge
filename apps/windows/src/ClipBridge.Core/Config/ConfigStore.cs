using System.Text;
using System.Text.Json;

namespace ClipBridge.Core.Config;

public enum ConfigFileState
{
    /// <summary>No config file yet — first run.</summary>
    Missing,

    /// <summary>Parsed and usable (possibly after normalization).</summary>
    Loaded,

    /// <summary>Exists but is not valid JSON / not a config object. Backed up, never overwritten silently.</summary>
    Malformed,

    /// <summary>Exists but cannot be read (permissions, lock, disconnected drive). Never overwritten.</summary>
    Unreadable,
}

public sealed record ConfigReadResult(
    ClientConfig Config,
    ConfigFileState State,
    string? BackupPath,
    string? Note);

/// <summary>
/// Durable, defensive config file access.
///
/// - Reads never throw: a broken file degrades to defaults plus a precise state.
/// - A malformed file is copied to a timestamped backup BEFORE anything can overwrite it,
///   so a hand-edited token is recoverable.
/// - Writes are atomic (temp file + replace), so a crash or power loss cannot leave a
///   half-written config.json that looks like "no configuration".
/// - Paths may contain spaces and Unicode; the file is UTF-8 without BOM.
/// </summary>
public static class ConfigStore
{
    /// <summary>
    /// 1 = pre-versioning (Milestone 1-4 configs, no "ConfigVersion" key).
    /// 2 = portability hardening: adds ConfigVersion/LanIpOverride, no key renames,
    ///     no token rotation — every 1.x file loads and keeps working as-is.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Never write a default-looking token placeholder; the real token is written as-is.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static ConfigReadResult Read(string path)
    {
        if (!File.Exists(path))
        {
            return new ConfigReadResult(new ClientConfig(), ConfigFileState.Missing, null, null);
        }

        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Preserve the file: an unreadable config is NOT a missing config.
            return new ConfigReadResult(
                new ClientConfig(), ConfigFileState.Unreadable, null,
                $"{path} could not be read: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ClientConfig>(json, ReadOptions);
            if (parsed is null)
            {
                var backup = BackupCorrupt(path, json);
                return new ConfigReadResult(
                    new ClientConfig(), ConfigFileState.Malformed, backup,
                    $"{path} contained no configuration object.");
            }
            var normalized = parsed.Normalize(out var notes);
            return new ConfigReadResult(
                normalized,
                ConfigFileState.Loaded,
                null,
                notes.Count == 0 ? null : string.Join(" ", notes));
        }
        catch (JsonException ex)
        {
            var backup = BackupCorrupt(path, json);
            return new ConfigReadResult(
                new ClientConfig(), ConfigFileState.Malformed, backup,
                $"{path} is not valid JSON ({ex.Message}).");
        }
    }

    /// <summary>Atomic write. Throws <see cref="ConfigWriteException"/> with an actionable message.</summary>
    public static void Write(ClientConfig config, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ConfigWriteException($"'{path}' has no parent directory.", path, null);
        }

        var persisted = config with { ConfigVersion = CurrentSchemaVersion };
        var json = JsonSerializer.Serialize(persisted, WriteOptions);
        // A config that could not be written must never be reported as written.
        try
        {
            Directory.CreateDirectory(directory);
            WriteAtomic(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            throw new ConfigWriteException(
                $"Could not write {path}: {ex.Message}. " +
                "Check that the folder is writable and not locked by another program.", path, ex);
        }
    }

    public static bool TryWrite(ClientConfig config, string path, out string? error)
    {
        try
        {
            Write(config, path);
            error = null;
            return true;
        }
        catch (ConfigWriteException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void WriteAtomic(string path, string json)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    // Some redirected/synced folders do not support Replace. A rename over the
                    // old file is still far safer than truncating it in place.
                    File.Move(temp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // leftover temp file is harmless; never mask the real error
            }
        }
    }

    /// <summary>
    /// Copies a broken config next to itself as config.corrupt-&lt;timestamp&gt;.json.
    /// Idempotent: an identical backup is reused instead of piling up copies.
    /// The copy keeps the original bytes, so nothing the user typed is lost.
    /// </summary>
    public static string? BackupCorrupt(string path, string? contents = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var bytes = contents is null ? File.ReadAllBytes(path) : Encoding.UTF8.GetBytes(contents);

            foreach (var existing in Directory.EnumerateFiles(directory, $"{name}.corrupt-*.json"))
            {
                try
                {
                    if (File.ReadAllBytes(existing).AsSpan().SequenceEqual(bytes))
                    {
                        return existing;
                    }
                }
                catch
                {
                    // unreadable old backup — ignore and continue
                }
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(directory, $"{name}.corrupt-{stamp}.json");
            var suffix = 1;
            while (File.Exists(target))
            {
                target = Path.Combine(directory, $"{name}.corrupt-{stamp}-{suffix++}.json");
            }
            File.WriteAllBytes(target, bytes);
            return target;
        }
        catch
        {
            // Backup is best-effort; the caller still reports the malformed state.
            return null;
        }
    }
}

public sealed class ConfigWriteException : Exception
{
    public string Path { get; }

    public ConfigWriteException(string message, string path, Exception? inner) : base(message, inner)
        => Path = path;
}

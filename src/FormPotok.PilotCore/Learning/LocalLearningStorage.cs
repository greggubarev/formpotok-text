using System.Text.Json;

namespace FormPotok.PilotCore.Learning;

internal sealed class LocalLearningStorage
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal LocalLearningStorage(string? localAppDataRoot)
    {
        var root = localAppDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("An absolute local application data root is required.", nameof(localAppDataRoot));
        }

        RootPath = Path.Combine(Path.GetFullPath(root), "FormPotok", "LocalLearning");
        ConsentPath = Path.Combine(RootPath, "consent.json");
        QueuePath = Path.Combine(RootPath, "statistics.json");
    }

    internal string RootPath { get; }
    internal string ConsentPath { get; }
    internal string QueuePath { get; }

    internal async Task<FileStream> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(RootPath);
        var lockPath = Path.Combine(RootPath, "storage.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // File sharing locks also serialize independent processes and release on a crash.
                var storageLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    File.Delete(ConsentPath + ".pending");
                    File.Delete(QueuePath + ".pending");
                    return storageLock;
                }
                catch
                {
                    storageLock.Dispose();
                    throw;
                }
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<LocalLearningConsent> ReadConsentAsync(CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(ConsentPath, 1024, cancellationToken).ConfigureAwait(false);
        if (bytes is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (HasExactProperties(root, "statistics", "examples", "schemaVersion", "consentVersion") &&
                    TryBoolean(root, "statistics", out var statistics) && TryBoolean(root, "examples", out var examples) &&
                    TryInt32(root, "schemaVersion", out var schema) && TryInt32(root, "consentVersion", out var version))
                {
                    var consent = new LocalLearningConsent(statistics, examples, schema, version);
                    if (consent.IsCurrent)
                    {
                        if (!consent.StatisticsEnabled)
                        {
                            ClearQueue();
                        }
                        return consent;
                    }
                }
            }
            catch (JsonException)
            {
                // Invalid settings never grant either permission.
            }
        }

        ClearQueue();
        return new LocalLearningConsent();
    }

    internal void ClearQueue()
    {
        File.Delete(QueuePath);
        File.Delete(QueuePath + ".pending");
    }

    internal async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".pending";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static async Task<byte[]?> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length == 0 || stream.Length > maximumBytes)
            {
                return null;
            }

            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    internal static bool HasExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
            {
                return false;
            }
        }
        return remaining.Count == 0;
    }

    internal static bool TryInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.GetProperty(name).ValueKind == JsonValueKind.Number && element.GetProperty(name).TryGetInt32(out value);
    }

    private static bool TryBoolean(JsonElement element, string name, out bool value)
    {
        var property = element.GetProperty(name);
        value = property.ValueKind == JsonValueKind.True;
        return property.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
}

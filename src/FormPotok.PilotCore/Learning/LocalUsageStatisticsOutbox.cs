using System.Globalization;
using System.Text.Json;

namespace FormPotok.PilotCore.Learning;

/// <summary>
/// Local-only queue: at most 128 events / 64 KiB; events expire after at most seven UTC calendar days.
/// Oldest events are evicted first; expired, invalid and unknown schemas are deleted on access.
/// No sender, credentials, automatic upload, or document content.
/// </summary>
public sealed class LocalUsageStatisticsOutbox
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumPendingCount = 128;
    public const int MaximumQueueBytes = 64 * 1024;
    public const int RetentionDays = 7;
    public const int MaximumPageCount = 1000;
    public const int MaximumBlockCount = 100000;
    public static TimeSpan MaximumDuration => TimeSpan.FromHours(1);

    private static readonly int[] PageBuckets = [0, 1, 2, 5, 10, 25, 50, 100, 250, 500, MaximumPageCount];
    private static readonly int[] BlockBuckets = [0, 1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 25000, 50000, MaximumBlockCount];
    private static readonly int[] DurationBuckets = [0, 1, 5, 15, 30, 60, 120, 300, 600, 1800, 3600];
    private readonly LocalLearningStorage _storage;
    private readonly TimeProvider _timeProvider;

    /// <param name="localAppDataRoot">Use the same root as LocalLearningConsentStore.</param>
    public LocalUsageStatisticsOutbox(string? localAppDataRoot = null, TimeProvider? timeProvider = null)
    {
        _storage = new LocalLearningStorage(localAppDataRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string StorageRootPath => _storage.RootPath;
    public string QueuePath => _storage.QueuePath;
    /// <summary>Synchronous local read; use ReadPendingAsync when the caller must not block.</summary>
    public int PendingCount => ReadPendingAsync().GetAwaiter().GetResult().Count;

    /// <summary>
    /// Both supplied and persisted consent must allow statistics. Pages are 0..1000,
    /// blocks 0..100000 and duration 0..1 hour; only upper-bound buckets are retained.
    /// </summary>
    public async Task<bool> RecordRecognitionAsync(
        LocalLearningConsent consent,
        int pageCount,
        int blockCount,
        TimeSpan duration,
        LocalRecognitionOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(consent);
        if (pageCount is < 0 or > MaximumPageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }
        if (blockCount is < 0 or > MaximumBlockCount)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount));
        }
        if (duration < TimeSpan.Zero || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        using var storageLock = await _storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var persisted = await _storage.ReadConsentAsync(cancellationToken).ConfigureAwait(false);
        if (!consent.StatisticsEnabled || !persisted.StatisticsEnabled)
        {
            return false;
        }

        var today = Today;
        var items = await ReadItemsAsync(today, cancellationToken).ConfigureAwait(false);
        var version = typeof(LocalUsageStatisticsOutbox).Assembly.GetName().Version;
        var productVersion = string.Create(CultureInfo.InvariantCulture,
            $"{version?.Major ?? 0}.{version?.Minor ?? 0}.{Math.Max(0, version?.Build ?? 0)}");
        var platform = OperatingSystem.IsWindows() ? LocalUsagePlatform.Windows :
            OperatingSystem.IsLinux() ? LocalUsagePlatform.Linux :
            OperatingSystem.IsMacOS() ? LocalUsagePlatform.MacOS : LocalUsagePlatform.Other;
        items.Add(new QueueItem(today + RetentionDays, new LocalUsageStatisticsEvent(
            CurrentSchemaVersion, productVersion, platform, outcome,
            Bucket(pageCount, PageBuckets), Bucket(blockCount, BlockBuckets), Bucket(duration.TotalSeconds, DurationBuckets))));
        while (items.Count > MaximumPendingCount)
        {
            items.RemoveAt(0);
        }
        await WriteItemsAsync(items, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<LocalUsageStatisticsEvent>> ReadPendingAsync(CancellationToken cancellationToken = default)
    {
        using var storageLock = await _storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (!(await _storage.ReadConsentAsync(cancellationToken).ConfigureAwait(false)).StatisticsEnabled)
        {
            return Array.Empty<LocalUsageStatisticsEvent>();
        }

        var items = await ReadItemsAsync(Today, cancellationToken).ConfigureAwait(false);
        await WriteItemsAsync(items, cancellationToken).ConfigureAwait(false);
        return items.Select(item => item.Event).ToArray();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var storageLock = await _storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _storage.ClearQueue();
    }

    private int Today => DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime).DayNumber;

    private async Task<List<QueueItem>> ReadItemsAsync(int today, CancellationToken cancellationToken)
    {
        var bytes = await LocalLearningStorage.ReadBoundedAsync(_storage.QueuePath, MaximumQueueBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (LocalLearningStorage.HasExactProperties(root, "schemaVersion", "items") &&
                    LocalLearningStorage.TryInt32(root, "schemaVersion", out var schema) && schema == CurrentSchemaVersion &&
                    root.GetProperty("items").ValueKind == JsonValueKind.Array &&
                    root.GetProperty("items").GetArrayLength() <= MaximumPendingCount)
                {
                    List<QueueItem> items = [];
                    foreach (var item in root.GetProperty("items").EnumerateArray())
                    {
                        if (!LocalLearningStorage.HasExactProperties(item, "expiresOnDay", "event") ||
                            !LocalLearningStorage.TryInt32(item, "expiresOnDay", out var expiry) || expiry < 0 || expiry > today + RetentionDays ||
                            !TryReadEvent(item.GetProperty("event"), out var usageEvent))
                        {
                            _storage.ClearQueue();
                            return [];
                        }
                        if (expiry > today)
                        {
                            items.Add(new QueueItem(expiry, usageEvent!));
                        }
                    }
                    return items;
                }
            }
            catch (JsonException)
            {
                // Unknown/corrupt data is deleted rather than forwarded or quarantined.
            }
        }

        _storage.ClearQueue();
        return [];
    }

    private static bool TryReadEvent(JsonElement element, out LocalUsageStatisticsEvent? usageEvent)
    {
        usageEvent = null;
        if (!LocalLearningStorage.HasExactProperties(element, "schemaVersion", "productVersion", "platform", "outcome",
            "pageCountBucket", "blockCountBucket", "durationSecondsBucket") ||
            !LocalLearningStorage.TryInt32(element, "schemaVersion", out var schema) || schema != CurrentSchemaVersion ||
            !LocalLearningStorage.TryInt32(element, "pageCountBucket", out var pages) || !PageBuckets.Contains(pages) ||
            !LocalLearningStorage.TryInt32(element, "blockCountBucket", out var blocks) || !BlockBuckets.Contains(blocks) ||
            !LocalLearningStorage.TryInt32(element, "durationSecondsBucket", out var duration) || !DurationBuckets.Contains(duration) ||
            element.GetProperty("productVersion").ValueKind != JsonValueKind.String ||
            element.GetProperty("platform").ValueKind != JsonValueKind.String ||
            element.GetProperty("outcome").ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var productVersion = element.GetProperty("productVersion").GetString();
        if (productVersion is null || productVersion.Length > 14 || !Version.TryParse(productVersion, out var version) ||
            version.Revision != -1 || version.Build < 0 || version.Major > 9999 || version.Minor > 9999 || version.Build > 9999 ||
            version.ToString(3) != productVersion)
        {
            return false;
        }
        var platform = element.GetProperty("platform").GetString() switch
        {
            "windows" => LocalUsagePlatform.Windows,
            "linux" => LocalUsagePlatform.Linux,
            "macOS" => LocalUsagePlatform.MacOS,
            "other" => LocalUsagePlatform.Other,
            _ => (LocalUsagePlatform)0,
        };
        var outcome = element.GetProperty("outcome").GetString() switch
        {
            "completed" => LocalRecognitionOutcome.Completed,
            "cancelled" => LocalRecognitionOutcome.Cancelled,
            "failed" => LocalRecognitionOutcome.Failed,
            _ => (LocalRecognitionOutcome)0,
        };
        if (!Enum.IsDefined(platform) || !Enum.IsDefined(outcome))
        {
            return false;
        }
        usageEvent = new LocalUsageStatisticsEvent(schema, productVersion, platform, outcome, pages, blocks, duration);
        return true;
    }

    private async Task WriteItemsAsync(List<QueueItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            _storage.ClearQueue();
            return;
        }

        var bytes = SerializeItems(items);
        while (bytes.Length > MaximumQueueBytes)
        {
            items.RemoveAt(0);
            bytes = SerializeItems(items);
        }
        await _storage.WriteAtomicallyAsync(_storage.QueuePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] SerializeItems(List<QueueItem> items) =>
        JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = CurrentSchemaVersion, Items = items }, LocalLearningStorage.JsonOptions);

    private static int Bucket(double value, int[] bounds) => bounds.First(bound => value <= bound);

    // Coarse expiry is local retention metadata, never part of the public statistics payload.
    private sealed record QueueItem(int ExpiresOnDay, LocalUsageStatisticsEvent Event);
}

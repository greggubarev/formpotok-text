using System.Text.Json;
using System.Text.Json.Nodes;
using FormPotok.PilotCore.Learning;

namespace FormPotok.PilotCore.Tests;

public sealed class LocalLearningStatisticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "formpotok-local-learning-tests", Guid.NewGuid().ToString("N"));
    private static readonly LocalLearningConsent Enabled = new(Statistics: true);

    [Fact]
    public async Task StatisticsRequireBothSavedAndSuppliedCurrentOptIn()
    {
        var store = new LocalLearningConsentStore(_root);
        var outbox = new LocalUsageStatisticsOutbox(_root);
        Assert.False(await RecordAsync(outbox, Enabled));
        await store.SaveAsync(new LocalLearningConsent(Examples: true));
        Assert.False(await RecordAsync(outbox, new LocalLearningConsent(true, true)));
        await store.SaveAsync(Enabled);
        Assert.False(await RecordAsync(outbox, new LocalLearningConsent()));
        Assert.False(await RecordAsync(outbox, new LocalLearningConsent(true, true, SchemaVersion: 2)));
        Assert.True(await RecordAsync(outbox, Enabled));
        Assert.Single(await new LocalUsageStatisticsOutbox(_root).ReadPendingAsync());
    }

    [Theory]
    [InlineData(LocalRecognitionOutcome.Completed)]
    [InlineData(LocalRecognitionOutcome.Cancelled)]
    [InlineData(LocalRecognitionOutcome.Failed)]
    public async Task TerminalOutcomes_AreTyped_AndMeasurementsAreBucketed(LocalRecognitionOutcome outcome)
    {
        var outbox = await CreateEnabledOutboxAsync();
        Assert.True(await outbox.RecordRecognitionAsync(Enabled, 3, 73, TimeSpan.FromMilliseconds(5123), outcome));
        var item = Assert.Single(await outbox.ReadPendingAsync());
        Assert.Equal(LocalUsageStatisticsOutbox.CurrentSchemaVersion, item.SchemaVersion);
        Assert.Equal(outcome, item.Outcome);
        Assert.Equal(5, item.PageCountBucket);
        Assert.Equal(100, item.BlockCountBucket);
        Assert.Equal(15, item.DurationSecondsBucket);
        Assert.True(Enum.IsDefined(item.Platform));
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", item.ProductVersion);
    }

    [Theory]
    [InlineData(-1, 0, 0, LocalRecognitionOutcome.Completed)]
    [InlineData(1001, 0, 0, LocalRecognitionOutcome.Completed)]
    [InlineData(int.MaxValue, 0, 0, LocalRecognitionOutcome.Completed)]
    [InlineData(1, -1, 0, LocalRecognitionOutcome.Completed)]
    [InlineData(1, 100001, 0, LocalRecognitionOutcome.Completed)]
    [InlineData(1, int.MaxValue, 0, LocalRecognitionOutcome.Completed)]
    [InlineData(1, 1, -1, LocalRecognitionOutcome.Completed)]
    [InlineData(1, 1, 3600001, LocalRecognitionOutcome.Completed)]
    [InlineData(1, 1, 1, (LocalRecognitionOutcome)0)]
    [InlineData(1, 1, 1, (LocalRecognitionOutcome)4)]
    public async Task InvalidMeasurementsOrOutcome_AreRejected_WithoutQueueMutation(int pages, int blocks, long milliseconds, LocalRecognitionOutcome outcome)
    {
        var outbox = await CreateEnabledOutboxAsync();
        await RecordAsync(outbox, Enabled);
        var before = await File.ReadAllBytesAsync(outbox.QueuePath);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            outbox.RecordRecognitionAsync(Enabled, pages, blocks, TimeSpan.FromMilliseconds(milliseconds), outcome));
        Assert.Equal(before, await File.ReadAllBytesAsync(outbox.QueuePath));
    }

    [Fact]
    public async Task ZeroAndMaximumMeasurements_AreAcceptedWithoutOverflow()
    {
        var outbox = await CreateEnabledOutboxAsync();
        await outbox.RecordRecognitionAsync(Enabled, 0, 0, TimeSpan.Zero, LocalRecognitionOutcome.Cancelled);
        await outbox.RecordRecognitionAsync(Enabled, 1000, 100000, TimeSpan.FromHours(1), LocalRecognitionOutcome.Completed);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => outbox.RecordRecognitionAsync(Enabled, 1, 1, TimeSpan.MaxValue, LocalRecognitionOutcome.Completed));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => outbox.RecordRecognitionAsync(Enabled, 1, 1, TimeSpan.MinValue, LocalRecognitionOutcome.Completed));
        var items = await outbox.ReadPendingAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal((0, 0, 0), (items[0].PageCountBucket, items[0].BlockCountBucket, items[0].DurationSecondsBucket));
        Assert.Equal((1000, 100000, 3600), (items[1].PageCountBucket, items[1].BlockCountBucket, items[1].DurationSecondsBucket));
    }

    [Fact]
    public async Task QueueIsBoundedByCountAndBytes_AndEvictsOldest()
    {
        var outbox = await CreateEnabledOutboxAsync();
        await RecordAsync(outbox, Enabled, LocalRecognitionOutcome.Failed);
        for (var i = 0; i < LocalUsageStatisticsOutbox.MaximumPendingCount + 5; i++)
        {
            await RecordAsync(outbox, Enabled);
            Assert.InRange(new FileInfo(outbox.QueuePath).Length, 1, LocalUsageStatisticsOutbox.MaximumQueueBytes);
        }
        Assert.Equal(LocalUsageStatisticsOutbox.MaximumPendingCount, outbox.PendingCount);
        Assert.All(await outbox.ReadPendingAsync(), item => Assert.Equal(LocalRecognitionOutcome.Completed, item.Outcome));
        Assert.Empty(Directory.GetFiles(outbox.StorageRootPath, "*.pending"));
    }

    [Fact]
    public async Task ExpiredEventsAreRemovedOnReadAndRecord_UsingOnlyCoarseLocalExpiry()
    {
        var clock = new TestClock();
        var outbox = await CreateEnabledOutboxAsync(clock);
        await RecordAsync(outbox, Enabled, LocalRecognitionOutcome.Failed);
        clock.Advance(TimeSpan.FromDays(LocalUsageStatisticsOutbox.RetentionDays - 1));
        Assert.Single(await outbox.ReadPendingAsync());
        await RecordAsync(outbox, Enabled);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(LocalRecognitionOutcome.Completed, Assert.Single(await outbox.ReadPendingAsync()).Outcome);
        clock.Advance(TimeSpan.FromDays(LocalUsageStatisticsOutbox.RetentionDays));
        Assert.Empty(await outbox.ReadPendingAsync());
        Assert.False(File.Exists(outbox.QueuePath));
        await RecordAsync(outbox, Enabled, LocalRecognitionOutcome.Failed);
        clock.Advance(TimeSpan.FromDays(LocalUsageStatisticsOutbox.RetentionDays));
        await RecordAsync(outbox, Enabled);
        Assert.Equal(LocalRecognitionOutcome.Completed, Assert.Single(await outbox.ReadPendingAsync()).Outcome);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("pageCountBucket", "3")]
    [InlineData("blockCountBucket", "-1")]
    [InlineData("durationSecondsBucket", "1.5")]
    [InlineData("durationSecondsBucket", "2147483648")]
    [InlineData("platform", "\"unknown-device\"")]
    [InlineData("platform", "1")]
    [InlineData("outcome", "\"exception text\"")]
    [InlineData("outcome", "1")]
    [InlineData("productVersion", "\"document contents\"")]
    [InlineData("productVersion", "\"0.4.2+workstation-id\"")]
    [InlineData("productVersion", "\"0.4.2.1\"")]
    [InlineData("productVersion", "\"00.4.2\"")]
    [InlineData("productVersion", "null")]
    public async Task PersistedInvalidEvent_IsDeleted_NotExposed(string property, string valueJson)
    {
        var outbox = await CreateEnabledOutboxAsync();
        await RecordAsync(outbox, Enabled);
        var queue = JsonNode.Parse(await File.ReadAllTextAsync(outbox.QueuePath))!;
        queue["items"]![0]!["event"]![property] = JsonNode.Parse(valueJson);
        await File.WriteAllTextAsync(outbox.QueuePath, queue.ToJsonString());
        Assert.Empty(await outbox.ReadPendingAsync());
        Assert.False(File.Exists(outbox.QueuePath));
    }

    [Fact]
    public async Task UnknownQueueSchema_MalformedJson_MissingAndDuplicateProperties_FailClosed()
    {
        var outbox = await CreateEnabledOutboxAsync();
        await RecordAsync(outbox, Enabled);
        var valid = await File.ReadAllTextAsync(outbox.QueuePath);
        var invalidQueues = new List<string> { "{not-json", "null", "[]", "{}", "" };
        var unknownSchema = JsonNode.Parse(valid)!;
        unknownSchema["schemaVersion"] = 2;
        invalidQueues.Add(unknownSchema.ToJsonString());
        var missingField = JsonNode.Parse(valid)!;
        missingField["items"]![0]!["event"]!.AsObject().Remove("outcome");
        invalidQueues.Add(missingField.ToJsonString());
        invalidQueues.Add(valid.Replace("\"outcome\":\"completed\"", "\"outcome\":\"failed\",\"outcome\":\"completed\"", StringComparison.Ordinal));
        foreach (var json in invalidQueues)
        {
            await File.WriteAllTextAsync(outbox.QueuePath, json);
            Assert.Empty(await outbox.ReadPendingAsync());
            Assert.False(File.Exists(outbox.QueuePath));
        }
    }

    [Fact]
    public async Task OversizedQueueOrTooManyPersistedItems_AreDeletedBeforeExposure()
    {
        var outbox = await CreateEnabledOutboxAsync();
        await File.WriteAllTextAsync(outbox.QueuePath, new string(' ', LocalUsageStatisticsOutbox.MaximumQueueBytes + 1));
        Assert.Empty(await outbox.ReadPendingAsync());
        Assert.False(File.Exists(outbox.QueuePath));

        await RecordAsync(outbox, Enabled);
        var queue = JsonNode.Parse(await File.ReadAllTextAsync(outbox.QueuePath))!;
        var item = queue["items"]![0]!.DeepClone();
        var items = queue["items"]!.AsArray();
        while (items.Count <= LocalUsageStatisticsOutbox.MaximumPendingCount)
        {
            items.Add(item.DeepClone());
        }
        await File.WriteAllTextAsync(outbox.QueuePath, queue.ToJsonString());
        Assert.Empty(await outbox.ReadPendingAsync());
        Assert.False(File.Exists(outbox.QueuePath));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidOrExcessivelyFutureExpiry_FailsClosed(int expiry)
    {
        var outbox = await CreateEnabledOutboxAsync();
        await RecordAsync(outbox, Enabled);
        var queue = JsonNode.Parse(await File.ReadAllTextAsync(outbox.QueuePath))!;
        queue["items"]![0]!["expiresOnDay"] = expiry;
        await File.WriteAllTextAsync(outbox.QueuePath, queue.ToJsonString());
        Assert.Empty(await outbox.ReadPendingAsync());
    }

    [Fact]
    public async Task GeneratedMeasurements_PreservePrivacyAllowlistAndBoundedBucketProperties()
    {
        var outbox = await CreateEnabledOutboxAsync();
        var random = new Random(20260928);
        var measurements = new List<(int Pages, int Blocks, double Seconds)> { (0, 0, 0), (1000, 100000, 3600), (3, 73, 5.123) };
        for (var i = 0; i < 48; i++)
        {
            measurements.Add((random.Next(0, 1001), random.Next(0, 100001), random.NextDouble() * 3600));
        }
        foreach (var (pages, blocks, seconds) in measurements)
        {
            await outbox.RecordRecognitionAsync(Enabled, pages, blocks, TimeSpan.FromSeconds(seconds), LocalRecognitionOutcome.Completed);
        }

        var events = await outbox.ReadPendingAsync();
        Assert.Equal(measurements.Count, events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            Assert.InRange(events[i].PageCountBucket, measurements[i].Pages, 1000);
            Assert.InRange(events[i].BlockCountBucket, measurements[i].Blocks, 100000);
            Assert.InRange((double)events[i].DurationSecondsBucket, measurements[i].Seconds, 3600);
            Assert.Contains(events[i].PageCountBucket, new[] { 0, 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000 });
            Assert.Contains(events[i].BlockCountBucket, new[] { 0, 1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 25000, 50000, 100000 });
            Assert.Contains(events[i].DurationSecondsBucket, new[] { 0, 1, 5, 15, 30, 60, 120, 300, 600, 1800, 3600 });
        }

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(events));
        foreach (var item in payload.RootElement.EnumerateArray())
        {
            Assert.Equal(new[] { "BlockCountBucket", "DurationSecondsBucket", "Outcome", "PageCountBucket", "Platform", "ProductVersion", "SchemaVersion" },
                item.EnumerateObject().Select(property => property.Name).Order().ToArray());
        }
        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(outbox.QueuePath));
        Assert.Equal(new[] { "items", "schemaVersion" }, file.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        foreach (var item in file.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(new[] { "event", "expiresOnDay" }, item.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(new[] { "blockCountBucket", "durationSecondsBucket", "outcome", "pageCountBucket", "platform", "productVersion", "schemaVersion" },
                item.GetProperty("event").EnumerateObject().Select(property => property.Name).Order().ToArray());
        }
    }

    [Theory]
    [InlineData("sourceFilename")]
    [InlineData("sourcePath")]
    [InlineData("sourceHash")]
    [InlineData("documentId")]
    [InlineData("ocrText")]
    [InlineData("crop")]
    [InlineData("engine")]
    [InlineData("exception")]
    [InlineData("deviceId")]
    [InlineData("userId")]
    [InlineData("timestamp")]
    [InlineData("arbitraryProperty")]
    public async Task UnknownFields_AreNeverRetainedAtAnyStorageLevel(string field)
    {
        var outbox = await CreateEnabledOutboxAsync();
        foreach (var level in new[] { "queue", "item", "event" })
        {
            await RecordAsync(outbox, Enabled);
            var queue = JsonNode.Parse(await File.ReadAllTextAsync(outbox.QueuePath))!;
            var target = level == "queue" ? queue : level == "item" ? queue["items"]![0]! : queue["items"]![0]!["event"]!;
            target[field] = "private-data-marker";
            await File.WriteAllTextAsync(outbox.QueuePath, queue.ToJsonString());
            Assert.Empty(await outbox.ReadPendingAsync());
            Assert.False(File.Exists(outbox.QueuePath));
        }
    }

    [Fact]
    public async Task ConcurrentInstances_DoNotLoseEvents_AndRevocationWinsAgainstStaleWriters()
    {
        var outbox = await CreateEnabledOutboxAsync();
        var writers = Enumerable.Range(0, 48).Select(_ => RecordAsync(new LocalUsageStatisticsOutbox(_root), Enabled)).ToArray();
        Assert.All(await Task.WhenAll(writers), Assert.True);
        Assert.Equal(48, outbox.PendingCount);

        var racingWriters = Enumerable.Range(0, 48).Select(_ => RecordAsync(new LocalUsageStatisticsOutbox(_root), Enabled)).ToArray();
        var revocation = new LocalLearningConsentStore(_root).SaveAsync(new LocalLearningConsent());
        await Task.WhenAll(racingWriters.Cast<Task>().Append(revocation));
        Assert.Empty(await outbox.ReadPendingAsync());
        Assert.False(File.Exists(outbox.QueuePath));
        Assert.False(await RecordAsync(outbox, Enabled));
    }

    [Fact]
    public async Task ClearAndCancellation_AreLocal_Idempotent_AndDoNotChangeConsent()
    {
        var outbox = await CreateEnabledOutboxAsync();
        await RecordAsync(outbox, Enabled);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecordAsync(outbox, Enabled, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outbox.ReadPendingAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outbox.ClearAsync(cancellation.Token));
        Assert.Single(await outbox.ReadPendingAsync());
        await outbox.ClearAsync();
        await outbox.ClearAsync();
        Assert.Equal(0, outbox.PendingCount);
        Assert.True((await new LocalLearningConsentStore(_root).LoadAsync()).StatisticsEnabled);
    }

    [Fact]
    public async Task RecordCancelledWhileWaitingForWriterLock_DoesNotLeaveTemporaryFile()
    {
        var outbox = await CreateEnabledOutboxAsync();
        using (var heldLock = new FileStream(Path.Combine(outbox.StorageRootPath, "storage.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancellation = new CancellationTokenSource();
            var recording = RecordAsync(outbox, Enabled, cancellationToken: cancellation.Token);
            Assert.False(recording.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording);
        }
        Assert.Equal(0, outbox.PendingCount);
        Assert.Empty(Directory.GetFiles(outbox.StorageRootPath, "*.pending"));
    }

    private async Task<LocalUsageStatisticsOutbox> CreateEnabledOutboxAsync(TimeProvider? clock = null)
    {
        await new LocalLearningConsentStore(_root).SaveAsync(Enabled);
        return new LocalUsageStatisticsOutbox(_root, clock);
    }

    private static Task<bool> RecordAsync(LocalUsageStatisticsOutbox outbox, LocalLearningConsent consent,
        LocalRecognitionOutcome outcome = LocalRecognitionOutcome.Completed, CancellationToken cancellationToken = default) =>
        outbox.RecordRecognitionAsync(consent, 1, 10, TimeSpan.FromSeconds(3), outcome, cancellationToken);

    public void Dispose()
    {
        var tempBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "formpotok-local-learning-tests")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(tempBase, Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}

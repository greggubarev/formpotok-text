using System.Text.Json;
using FormPotok.PilotCore.Learning;

namespace FormPotok.PilotCore.Tests;

public sealed class LocalLearningConsentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "formpotok-local-learning-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Defaults_AreOff_AndRootIsLocalAppData()
    {
        var consent = new LocalLearningConsent();
        Assert.True(consent.IsCurrent);
        Assert.False(consent.StatisticsEnabled);
        Assert.False(consent.ExamplesEnabled);
        Assert.Equal(consent, await new LocalLearningConsentStore(_root).LoadAsync());

        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FormPotok", "LocalLearning");
        Assert.Equal(expected, new LocalLearningConsentStore().StorageRootPath);
        Assert.Equal(expected, new LocalUsageStatisticsOutbox().StorageRootPath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConsentFlags_AreIndependent_AndRoundtripAcrossInstances(bool statistics, bool examples)
    {
        var consent = new LocalLearningConsent(statistics, examples);
        var store = new LocalLearningConsentStore(_root);
        await store.SaveAsync(consent);
        var loaded = await new LocalLearningConsentStore(_root).LoadAsync();
        Assert.Equal(consent, loaded);
        Assert.Equal(statistics, loaded.StatisticsEnabled);
        Assert.Equal(examples, loaded.ExamplesEnabled);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(store.ConsentPath));
        Assert.Equal(new[] { "consentVersion", "examples", "schemaVersion", "statistics" },
            document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Empty(Directory.GetFiles(store.StorageRootPath, "*.pending"));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"statistics\":true,\"examples\":true}")]
    [InlineData("{\"statistics\":true,\"examples\":true,\"schemaVersion\":2,\"consentVersion\":1}")]
    [InlineData("{\"statistics\":true,\"examples\":true,\"schemaVersion\":1,\"consentVersion\":2}")]
    [InlineData("{\"statistics\":true,\"examples\":true,\"schemaVersion\":1,\"consentVersion\":0}")]
    [InlineData("{\"statistics\":\"true\",\"examples\":true,\"schemaVersion\":1,\"consentVersion\":1}")]
    [InlineData("{\"statistics\":true,\"examples\":null,\"schemaVersion\":1,\"consentVersion\":1}")]
    [InlineData("{\"statistics\":true,\"examples\":true,\"schemaVersion\":1,\"consentVersion\":1,\"other\":true}")]
    [InlineData("{\"statistics\":false,\"statistics\":true,\"examples\":true,\"schemaVersion\":1,\"consentVersion\":1}")]
    [InlineData("{\"statistics\":true,\"examples\":true,\"schemaVersion\":1.5,\"consentVersion\":1}")]
    [InlineData("{\"statistics\":true,\"examples\":true,\"schemaVersion\":2147483648,\"consentVersion\":1}")]
    public async Task CorruptOrUnknownConsent_IsOff_AndDeletesQueuedStatistics(string json)
    {
        var store = new LocalLearningConsentStore(_root);
        var outbox = new LocalUsageStatisticsOutbox(_root);
        var consent = new LocalLearningConsent(Statistics: true, Examples: true);
        await store.SaveAsync(consent);
        Assert.True(await RecordAsync(outbox, consent));
        await File.WriteAllTextAsync(store.ConsentPath, json);
        Assert.Equal(new LocalLearningConsent(), await store.LoadAsync());
        Assert.False(File.Exists(outbox.QueuePath));
        Assert.False(await RecordAsync(outbox, consent));
    }

    [Fact]
    public async Task OversizedOrMissingConsent_IsOff_AndClearsQueue()
    {
        var store = new LocalLearningConsentStore(_root);
        var outbox = new LocalUsageStatisticsOutbox(_root);
        var consent = new LocalLearningConsent(Statistics: true);
        await store.SaveAsync(consent);
        await RecordAsync(outbox, consent);
        await File.WriteAllTextAsync(store.ConsentPath, new string(' ', 1025));
        Assert.False((await store.LoadAsync()).StatisticsEnabled);
        Assert.False(File.Exists(outbox.QueuePath));
        await store.SaveAsync(consent);
        await RecordAsync(outbox, consent);
        File.Delete(store.ConsentPath);
        Assert.Empty(await outbox.ReadPendingAsync());
        Assert.False(File.Exists(outbox.QueuePath));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    public async Task UnsupportedVersion_CannotGrantPermission_EvenWhenSaved(int schema, int version)
    {
        var consent = new LocalLearningConsent(true, true, schema, version);
        Assert.False(consent.StatisticsEnabled);
        Assert.False(consent.ExamplesEnabled);
        var store = new LocalLearningConsentStore(_root);
        await store.SaveAsync(consent);
        Assert.Equal(new LocalLearningConsent(), await store.LoadAsync());
        Assert.False(await RecordAsync(new LocalUsageStatisticsOutbox(_root), consent));
    }

    [Fact]
    public async Task StatisticsRevocation_ClearsQueue_AndStaleConsentCannotRecreateIt()
    {
        var store = new LocalLearningConsentStore(_root);
        var outbox = new LocalUsageStatisticsOutbox(_root);
        var oldConsent = new LocalLearningConsent(Statistics: true);
        await store.SaveAsync(oldConsent);
        await RecordAsync(outbox, oldConsent);
        await File.WriteAllTextAsync(outbox.QueuePath + ".pending", "interrupted local statistics");
        await store.SaveAsync(new LocalLearningConsent(Examples: true));

        Assert.False(File.Exists(outbox.QueuePath));
        Assert.False(File.Exists(outbox.QueuePath + ".pending"));
        Assert.False(await RecordAsync(new LocalUsageStatisticsOutbox(_root), oldConsent));
        Assert.Equal(0, outbox.PendingCount);
        Assert.True((await store.LoadAsync()).ExamplesEnabled);
        await store.SaveAsync(oldConsent);
        Assert.Empty(await outbox.ReadPendingAsync());
    }

    [Fact]
    public async Task ExamplesRevocation_DoesNotRevokeStatistics()
    {
        var store = new LocalLearningConsentStore(_root);
        var consent = new LocalLearningConsent(true, true);
        await store.SaveAsync(consent);
        var outbox = new LocalUsageStatisticsOutbox(_root);
        await RecordAsync(outbox, consent);
        await store.SaveAsync(new LocalLearningConsent(Statistics: true));
        Assert.Single(await outbox.ReadPendingAsync());
    }

    [Fact]
    public void AllConsentCombinations_RequireSeparateExampleApproval_AndNeverAuthorizeAutomaticSharing()
    {
        foreach (var schema in new[] { 0, 1, 2 })
        foreach (var version in new[] { 0, 1, 2 })
        foreach (var statistics in new[] { false, true })
        foreach (var examples in new[] { false, true })
        {
            var consent = new LocalLearningConsent(statistics, examples, schema, version);
            Assert.Equal(schema == 1 && version == 1 && examples, LocalLearningExamplePolicy.CanRequestApproval(consent));
            Assert.False(LocalLearningExamplePolicy.AllowsAutomaticUpload(consent));
            Assert.Equal(LocalExampleApprovalRequirement.SeparatePerExampleApproval, LocalLearningExamplePolicy.ApprovalRequirement);
            Assert.True(LocalLearningExamplePolicy.TextMayContainSensitiveData);
        }
    }

    [Fact]
    public async Task CancelledSave_DoesNotChangeExistingConsentOrStatistics()
    {
        var store = new LocalLearningConsentStore(_root);
        var consent = new LocalLearningConsent(true, true);
        await store.SaveAsync(consent);
        var outbox = new LocalUsageStatisticsOutbox(_root);
        await RecordAsync(outbox, consent);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new LocalLearningConsent(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
        Assert.Equal(consent, await store.LoadAsync());
        Assert.Single(await outbox.ReadPendingAsync());
    }

    [Fact]
    public async Task CancelledLockWait_PreservesLastCommittedConsent_AndIgnoresInterruptedWrite()
    {
        var store = new LocalLearningConsentStore(_root);
        var consent = new LocalLearningConsent(true, false);
        await store.SaveAsync(consent);
        using (var heldLock = new FileStream(Path.Combine(store.StorageRootPath, "storage.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await File.WriteAllTextAsync(store.ConsentPath + ".pending", "{broken interrupted settings");
            using var cancellation = new CancellationTokenSource();
            var saving = new LocalLearningConsentStore(_root).SaveAsync(new LocalLearningConsent(), cancellation.Token);
            Assert.False(saving.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        }
        Assert.Equal(consent, await store.LoadAsync());
        Assert.False(File.Exists(store.ConsentPath + ".pending"));
    }

    private static Task<bool> RecordAsync(LocalUsageStatisticsOutbox outbox, LocalLearningConsent consent) =>
        outbox.RecordRecognitionAsync(consent, 1, 10, TimeSpan.FromSeconds(3), LocalRecognitionOutcome.Completed);

    public void Dispose()
    {
        var tempBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "formpotok-local-learning-tests")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(tempBase, Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

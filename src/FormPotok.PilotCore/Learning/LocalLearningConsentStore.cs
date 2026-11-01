using System.Text.Json;

namespace FormPotok.PilotCore.Learning;

public sealed class LocalLearningConsentStore
{
    private readonly LocalLearningStorage _storage;

    /// <param name="localAppDataRoot">Defaults to LocalApplicationData; override for isolated local storage/tests.</param>
    public LocalLearningConsentStore(string? localAppDataRoot = null) => _storage = new LocalLearningStorage(localAppDataRoot);

    public string StorageRootPath => _storage.RootPath;
    public string ConsentPath => _storage.ConsentPath;

    public async Task<LocalLearningConsent> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var storageLock = await _storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await _storage.ReadConsentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(LocalLearningConsent consent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        using var storageLock = await _storage.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var previous = await _storage.ReadConsentAsync(cancellationToken).ConfigureAwait(false);
        var current = consent.IsCurrent ? consent : new LocalLearningConsent();
        if (!previous.StatisticsEnabled || !current.StatisticsEnabled)
        {
            // Clear before enabling a new grant, and before a revocation can be cancelled.
            _storage.ClearQueue();
        }

        await _storage.WriteAtomicallyAsync(_storage.ConsentPath,
            JsonSerializer.SerializeToUtf8Bytes(current, LocalLearningStorage.JsonOptions), cancellationToken).ConfigureAwait(false);
        if (!current.StatisticsEnabled)
        {
            // Once the off setting is committed, cleanup must not be interrupted by cancellation.
            _storage.ClearQueue();
        }
    }
}

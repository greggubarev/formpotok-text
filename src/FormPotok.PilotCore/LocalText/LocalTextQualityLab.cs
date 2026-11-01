using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace FormPotok.PilotCore.LocalText;

public sealed record LocalQualitySample(
    string SourceSha256,
    string SourceExtension,
    string ReferenceSha256,
    string BaselineSha256,
    string Split,
    int Version,
    string? SourceGroupSha256 = null);

public sealed record LocalQualityCase(LocalQualitySample Sample, string ReferenceText, string BaselineText);
public sealed record LocalQualitySnapshot(int SchemaVersion, IReadOnlyList<LocalQualitySample> Samples);

public sealed class LocalTextQualityLab(string root)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg" };
    private readonly string _root = Path.GetFullPath(root);
    private readonly SemaphoreSlim _writer = new(1, 1);

    public string Root => _root;

    public async Task<IReadOnlyList<LocalQualitySample>> ListSamplesAsync(CancellationToken cancellationToken = default)
    {
        var samplesRoot = Path.Combine(_root, "samples");
        if (!Directory.Exists(samplesRoot)) return [];
        var samples = new List<LocalQualitySample>();
        foreach (var path in Directory.EnumerateFiles(samplesRoot, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (samples.Count >= LocalTextWorkspace.MaximumDocuments)
                throw new InvalidDataException("Reference count exceeds the local limit.");
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Reference metadata exceeds the size limit.");
            var sample = JsonSerializer.Deserialize<LocalQualitySample>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
                ?? throw new InvalidDataException("Reference metadata is empty.");
            var sourceHash = Path.GetFileNameWithoutExtension(path);
            if (!IsSha256(sourceHash)) throw new InvalidDataException("Reference metadata filename is invalid.");
            ValidateSample(sample, sourceHash);
            samples.Add(sample);
        }
        return samples;
    }

    public async IAsyncEnumerable<LocalQualityCase> StreamCasesAsync(string split,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (split is not ("holdout" or "development")) throw new ArgumentException("Unknown dataset split.", nameof(split));
        foreach (var sample in await ListSamplesAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sample.Split != split) continue;
            yield return await LoadCaseAsync(sample, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<LocalQualityCase>> LoadCasesAsync(string split, CancellationToken cancellationToken = default)
    {
        var cases = new List<LocalQualityCase>();
        await foreach (var entry in StreamCasesAsync(split, cancellationToken)) cases.Add(entry);
        return cases;
    }

    public async Task<LocalQualitySample> AssignSourceGroupAsync(string sourceHash, string groupName,
        CancellationToken cancellationToken = default)
    {
        if (!IsSha256(sourceHash)) throw new ArgumentException("Expected a lowercase SHA-256 digest.", nameof(sourceHash));
        var groupHash = HashSourceGroup(groupName);
        await _writer.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(_root, "samples", sourceHash + ".json");
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Reference metadata exceeds the size limit.");
            var sample = JsonSerializer.Deserialize<LocalQualitySample>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
                ?? throw new InvalidDataException("Reference metadata is empty.");
            ValidateSample(sample, sourceHash);
            if (sample.SourceGroupSha256 == groupHash) return sample;
            var updated = sample with
            {
                SourceGroupSha256 = groupHash,
                Split = SplitForSource(groupHash),
                Version = checked(sample.Version + 1)
            };
            await WriteAtomicallyAsync(path, JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions), cancellationToken);
            return updated;
        }
        finally { _writer.Release(); }
    }

    public async Task<string> FreezeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            var samples = await ListSamplesAsync(cancellationToken);
            if (samples.Count == 0 || samples.Any(sample => sample.SourceGroupSha256 is null))
                throw new InvalidOperationException("Every reference needs a source group before freezing a snapshot.");
            var snapshot = new LocalQualitySnapshot(1, samples.OrderBy(sample => sample.SourceSha256, StringComparer.Ordinal).ToArray());
            foreach (var sample in snapshot.Samples)
                await LoadCaseAsync(sample, cancellationToken);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await WriteContentOnceAsync(Path.Combine(_root, "snapshots", hash + ".json"), bytes, cancellationToken);
            return hash;
        }
        finally { _writer.Release(); }
    }

    public async IAsyncEnumerable<LocalQualityCase> StreamSnapshotCasesAsync(string snapshotHash, string split,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsSha256(snapshotHash)) throw new ArgumentException("Expected a lowercase snapshot SHA-256.", nameof(snapshotHash));
        if (split is not ("holdout" or "development")) throw new ArgumentException("Unknown dataset split.", nameof(split));
        var path = Path.Combine(_root, "snapshots", snapshotHash + ".json");
        if (new FileInfo(path).Length > 256 * 1024 * 1024) throw new InvalidDataException("Snapshot exceeds the size limit.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != snapshotHash)
            throw new InvalidDataException("Snapshot content does not match its digest.");
        var snapshot = JsonSerializer.Deserialize<LocalQualitySnapshot>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Snapshot is empty.");
        if (snapshot.SchemaVersion != 1 || snapshot.Samples is null || snapshot.Samples.Count is < 1 or > LocalTextWorkspace.MaximumDocuments)
            throw new InvalidDataException("Snapshot schema is invalid.");
        string? previous = null;
        foreach (var sample in snapshot.Samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sample is null || sample.SourceGroupSha256 is null || previous is not null
                && StringComparer.Ordinal.Compare(previous, sample.SourceSha256) >= 0)
                throw new InvalidDataException("Snapshot entries are invalid or unsorted.");
            ValidateSample(sample, sample.SourceSha256);
            previous = sample.SourceSha256;
            if (sample.Split == split) yield return await LoadCaseAsync(sample, cancellationToken);
        }
    }

    public static string HashSourceGroup(string groupName)
    {
        if (groupName is null) throw new ArgumentNullException(nameof(groupName));
        var normalized = groupName.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        if (normalized.Length is < 1 or > 128 || normalized.Any(char.IsControl))
            throw new ArgumentException("Group name must be 1-128 printable characters.", nameof(groupName));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("formpotok-source-group-v1\0" + normalized)));
    }

    public async Task<LocalQualitySample> SaveReviewedAsync(
        LocalTextDocument document, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (document.Status != LocalTextStatus.Reviewed || document.Extraction is null || string.IsNullOrWhiteSpace(document.EditedText))
            throw new InvalidOperationException("Only confirmed nonempty text can become a reference.");
        if (document.SourceHash.Length != 64 || document.SourceHash.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Invalid source hash.");
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!Extensions.Contains(extension)) throw new InvalidDataException("Unsupported source format.");
        var reference = Encoding.UTF8.GetBytes(document.EditedText);
        var baseline = Encoding.UTF8.GetBytes(document.OriginalText);
        if (reference.Length > 10 * 1024 * 1024 || baseline.Length > 10 * 1024 * 1024)
            throw new InvalidDataException("Reference exceeds the local size limit.");

        await _writer.WaitAsync(cancellationToken);
        try
        {
            await using (var source = File.OpenRead(sourcePath))
            {
                if (source.Length is <= 0 or > LocalTextWorkspace.MaximumSourceBytes)
                    throw new InvalidDataException("Source exceeds the local size limit.");
            }
            await VerifyHashAsync(sourcePath, document.SourceHash, cancellationToken);
            Directory.CreateDirectory(_root);
            var samplePath = Path.Combine(_root, "samples", document.SourceHash + ".json");
            if (File.Exists(samplePath) && new FileInfo(samplePath).Length > 64 * 1024)
                throw new InvalidDataException("Reference metadata exceeds the size limit.");
            var existing = File.Exists(samplePath)
                ? JsonSerializer.Deserialize<LocalQualitySample>(await File.ReadAllTextAsync(samplePath, cancellationToken), JsonOptions)
                    ?? throw new InvalidDataException("Reference metadata is empty.")
                : null;
            if (existing is not null)
            {
                ValidateSample(existing, document.SourceHash);
                await ReadTextAsync(Path.Combine(_root, "references", existing.SourceSha256, existing.ReferenceSha256 + ".txt"),
                    existing.ReferenceSha256, cancellationToken);
                await ReadTextAsync(Path.Combine(_root, "baselines", existing.SourceSha256, existing.BaselineSha256 + ".txt"),
                    existing.BaselineSha256, cancellationToken);
            }
            var sourceExtension = existing?.SourceExtension ?? extension;
            var savedSource = Path.Combine(_root, "sources", document.SourceHash + sourceExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(savedSource)!);
            if (!File.Exists(savedSource))
            {
                var temporary = savedSource + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var input = File.OpenRead(sourcePath))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (input.Length is <= 0 or > LocalTextWorkspace.MaximumSourceBytes)
                            throw new InvalidDataException("Source exceeds the local size limit.");
                        await input.CopyToAsync(output, cancellationToken);
                    }
                    await VerifyHashAsync(temporary, document.SourceHash, cancellationToken);
                    File.Move(temporary, savedSource);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else await VerifyHashAsync(savedSource, document.SourceHash, cancellationToken);

            var referenceHash = Convert.ToHexStringLower(SHA256.HashData(reference));
            var baselineHash = Convert.ToHexStringLower(SHA256.HashData(baseline));
            await WriteContentOnceAsync(Path.Combine(_root, "references", document.SourceHash, referenceHash + ".txt"), reference, cancellationToken);
            await WriteContentOnceAsync(Path.Combine(_root, "baselines", document.SourceHash, baselineHash + ".txt"), baseline, cancellationToken);
            if (existing is not null && existing.ReferenceSha256 == referenceHash && existing.BaselineSha256 == baselineHash)
                return existing;

            var sample = new LocalQualitySample(document.SourceHash, sourceExtension, referenceHash, baselineHash,
                SplitForSource(existing?.SourceGroupSha256 ?? document.SourceHash), (existing?.Version ?? 0) + 1,
                existing?.SourceGroupSha256);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(sample, JsonOptions);
            await WriteAtomicallyAsync(samplePath, metadata, cancellationToken);
            return sample;
        }
        finally { _writer.Release(); }
    }

    public static string SplitForSource(string sha256)
    {
        if (sha256.Length != 64 || sha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Expected a SHA-256 hex digest.", nameof(sha256));
        return Convert.ToByte(sha256[..2], 16) % 5 == 0 ? "holdout" : "development";
    }

    private static void ValidateSample(LocalQualitySample sample, string expectedSourceHash)
    {
        if (sample.SourceSha256 != expectedSourceHash || !Extensions.Contains(sample.SourceExtension)
            || !IsSha256(sample.ReferenceSha256) || !IsSha256(sample.BaselineSha256)
            || sample.SourceExtension != sample.SourceExtension.ToLowerInvariant()
            || sample.SourceGroupSha256 is not null && !IsSha256(sample.SourceGroupSha256)
            || sample.Split != SplitForSource(sample.SourceGroupSha256 ?? expectedSourceHash) || sample.Version is < 1 or int.MaxValue)
            throw new InvalidDataException("Reference metadata is invalid.");
    }

    private async Task<LocalQualityCase> LoadCaseAsync(LocalQualitySample sample, CancellationToken cancellationToken)
    {
        await VerifyHashAsync(Path.Combine(_root, "sources", sample.SourceSha256 + sample.SourceExtension),
            sample.SourceSha256, cancellationToken);
        var reference = await ReadTextAsync(Path.Combine(_root, "references", sample.SourceSha256, sample.ReferenceSha256 + ".txt"),
            sample.ReferenceSha256, cancellationToken);
        var baseline = await ReadTextAsync(Path.Combine(_root, "baselines", sample.SourceSha256, sample.BaselineSha256 + ".txt"),
            sample.BaselineSha256, cancellationToken);
        return new LocalQualityCase(sample, reference, baseline);
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    private static async Task VerifyHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source does not match the reviewed document.");
    }

    private static async Task<string> ReadTextAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > 10 * 1024 * 1024)
            throw new InvalidDataException("Reference content exceeds the size limit.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reference content does not match its digest.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static async Task WriteContentOnceAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var stored = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(stored)))
                throw new InvalidDataException("Stored reference content is damaged.");
            return;
        }
        await WriteAtomicallyAsync(path, bytes, cancellationToken, overwrite: false);
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

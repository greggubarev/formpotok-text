using System.Security.Cryptography;
using System.Text;

namespace FormPotok.PilotCore.LocalText;

public sealed record LocalQualityPrediction(
    string Candidate,
    string SourceSha256,
    string? Text,
    double? DurationMilliseconds = null,
    long? PeakMemoryBytes = null,
    string? Error = null);

public sealed record LocalQualityItem(
    string SourceSha256,
    string ReferenceSha256,
    string Status,
    int? CharacterEdits,
    int ReferenceCharacters,
    int? WordEdits,
    int ReferenceWords,
    double? DurationMilliseconds,
    long? PeakMemoryBytes);

public sealed record LocalQualityCandidateReport(
    string Candidate,
    bool Complete,
    int Completed,
    int Missing,
    int Failed,
    int Empty,
    double? Cer,
    double? Wer,
    double? P95DurationMilliseconds,
    long? P95PeakMemoryBytes,
    IReadOnlyList<LocalQualityItem> Items);

public sealed record LocalQualityReport(
    string Split,
    string DatasetSha256,
    string NormalizationPolicy,
    int SourceDocuments,
    bool MinimumHoldoutFileCountMet,
    IReadOnlyList<LocalQualityCandidateReport> Candidates,
    string? SnapshotSha256 = null,
    int SourceGroups = 0,
    bool MinimumHoldoutGroupCountMet = false);

public static class LocalQualityEvaluator
{
    private const long MaximumComparisonCells = 25_000_000;
    public const string NormalizationPolicy = "nfc-lower-invariant-collapse-whitespace-runes-v1";

    public static async Task<LocalQualityReport> EvaluateAsync(LocalTextQualityLab lab,
        IReadOnlyList<LocalQualityPrediction> predictions, string split = "holdout", CancellationToken cancellationToken = default)
        => await EvaluateCasesAsync(lab.StreamCasesAsync(split, cancellationToken), predictions, split, null, cancellationToken);

    public static async Task<LocalQualityReport> EvaluateSnapshotAsync(LocalTextQualityLab lab, string snapshotHash,
        IReadOnlyList<LocalQualityPrediction> predictions, string split = "holdout", CancellationToken cancellationToken = default)
        => await EvaluateCasesAsync(lab.StreamSnapshotCasesAsync(snapshotHash, split, cancellationToken),
            predictions, split, snapshotHash, cancellationToken);

    private static async Task<LocalQualityReport> EvaluateCasesAsync(IAsyncEnumerable<LocalQualityCase> cases,
        IReadOnlyList<LocalQualityPrediction> predictions, string split, string? snapshotHash, CancellationToken cancellationToken)
    {
        if (predictions.Any(prediction => prediction is null || prediction.Candidate is null || prediction.SourceSha256 is null
                || prediction.Candidate.Length is < 1 or > 64
                || prediction.Candidate.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
                || prediction.SourceSha256.Length != 64 || prediction.SourceSha256.Any(character => !char.IsAsciiHexDigit(character))
                || prediction.DurationMilliseconds is < 0 || prediction.DurationMilliseconds is double.NaN or double.PositiveInfinity or double.NegativeInfinity
                || prediction.PeakMemoryBytes is < 0 || prediction.Text?.Length > 10_000_000))
            throw new ArgumentException("Invalid prediction metadata.", nameof(predictions));
        var candidateNames = predictions.Select(prediction => prediction.Candidate)
            .Append("current-at-review").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (predictions.Any(prediction => prediction.Candidate == "current-at-review"))
            throw new ArgumentException("The baseline candidate name is reserved.", nameof(predictions));
        var indexed = new Dictionary<(string Candidate, string Hash), LocalQualityPrediction>();
        foreach (var prediction in predictions)
        {
            if (!indexed.TryAdd((prediction.Candidate, prediction.SourceSha256.ToLowerInvariant()), prediction))
                throw new ArgumentException("Duplicate candidate prediction for a source.", nameof(predictions));
        }

        var itemsByCandidate = candidateNames.ToDictionary(name => name,
            _ => new List<LocalQualityItem>(), StringComparer.Ordinal);
        using var fingerprintHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sourceCount = 0;
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var allGrouped = true;
        await foreach (var entry in cases)
        {
            if (entry.Sample.SourceGroupSha256 is { } group) groups.Add(group);
            else allGrouped = false;
            if (sourceCount++ > 0) fingerprintHasher.AppendData([(byte)'\n']);
            fingerprintHasher.AppendData(Encoding.UTF8.GetBytes(entry.Sample.SourceSha256 + ":" + entry.Sample.ReferenceSha256
                + ":" + (entry.Sample.SourceGroupSha256 ?? "ungrouped") + ":" + entry.Sample.Version));
            foreach (var name in candidateNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prediction = name == "current-at-review"
                    ? new LocalQualityPrediction(name, entry.Sample.SourceSha256, entry.BaselineText)
                    : indexed.GetValueOrDefault((name, entry.Sample.SourceSha256));
                itemsByCandidate[name].Add(Compare(entry, prediction));
            }
        }
        var fingerprint = Convert.ToHexStringLower(fingerprintHasher.GetHashAndReset());
        var reports = new List<LocalQualityCandidateReport>();
        foreach (var name in candidateNames)
        {
            var items = itemsByCandidate[name];
            var completed = items.Where(item => item.Status == "ok").ToArray();
            var characters = completed.Sum(item => (long)item.ReferenceCharacters);
            var words = completed.Sum(item => (long)item.ReferenceWords);
            var durations = completed.Where(item => item.DurationMilliseconds.HasValue)
                .Select(item => item.DurationMilliseconds!.Value).Order().ToArray();
            var memories = completed.Where(item => item.PeakMemoryBytes.HasValue)
                .Select(item => item.PeakMemoryBytes!.Value).Order().ToArray();
            var complete = items.Count > 0 && completed.Length == items.Count;
            reports.Add(new LocalQualityCandidateReport(name, complete, completed.Length,
                items.Count(item => item.Status == "missing"), items.Count(item => item.Status is "error" or "metric-limit"),
                items.Count(item => item.Status == "empty"),
                complete && characters > 0 ? completed.Sum(item => (long)item.CharacterEdits!.Value) / (double)characters : null,
                complete && words > 0 ? completed.Sum(item => (long)item.WordEdits!.Value) / (double)words : null,
                complete && durations.Length == items.Count ? Percentile95(durations) : null,
                complete && memories.Length == items.Count ? Percentile95(memories) : null, items));
        }
        return new LocalQualityReport(split, fingerprint, NormalizationPolicy, sourceCount,
            split == "holdout" && sourceCount >= 30, reports, snapshotHash, groups.Count,
            split == "holdout" && allGrouped && groups.Count >= 30);
    }

    private static LocalQualityItem Compare(LocalQualityCase entry, LocalQualityPrediction? prediction)
    {
        var reference = Normalize(entry.ReferenceText);
        var referenceRunes = reference.EnumerateRunes().ToArray();
        var referenceWords = Words(reference);
        var status = prediction is null ? "missing" : prediction.Error is not null ? "error"
            : string.IsNullOrWhiteSpace(prediction.Text) ? "empty" : "ok";
        int? charEdits = null;
        int? wordEdits = null;
        if (status == "ok")
        {
            var actual = Normalize(prediction!.Text!);
            var actualRunes = actual.EnumerateRunes().ToArray();
            var actualWords = Words(actual);
            charEdits = Distance(referenceRunes.AsSpan(), actualRunes.AsSpan());
            wordEdits = Distance(referenceWords.AsSpan(), actualWords.AsSpan());
            if (charEdits is null || wordEdits is null) status = "metric-limit";
        }
        return new LocalQualityItem(entry.Sample.SourceSha256, entry.Sample.ReferenceSha256, status,
            charEdits, referenceRunes.Length, wordEdits, referenceWords.Length,
            prediction?.DurationMilliseconds, prediction?.PeakMemoryBytes);
    }

    private static string Normalize(string text) => string.Join(' ', text.Normalize(NormalizationForm.FormC)
        .ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int? Distance<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right) where T : IEquatable<T>
    {
        while (left.Length > 0 && right.Length > 0 && left[0].Equals(right[0])) { left = left[1..]; right = right[1..]; }
        while (left.Length > 0 && right.Length > 0 && left[^1].Equals(right[^1])) { left = left[..^1]; right = right[..^1]; }
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;
        if ((long)left.Length * right.Length > MaximumComparisonCells) return null;
        if (right.Length > left.Length)
        {
            var swapped = left;
            left = right;
            right = swapped;
        }
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++) previous[column] = column;
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + (left[row - 1].Equals(right[column - 1]) ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static T? Percentile95<T>(T[] sorted) where T : struct =>
        sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
}

using System.Security.Cryptography;
using System.Text;

namespace FormPotok.PilotCore.LocalText;

public sealed record LocalQualityLeakagePair(
    string DevelopmentSourceSha256,
    string HoldoutSourceSha256,
    string DevelopmentGroupSha256,
    string HoldoutGroupSha256,
    double Similarity,
    string Reason);

public sealed record LocalQualityLeakageReport(
    string SnapshotSha256,
    int DevelopmentDocuments,
    int HoldoutDocuments,
    int ComparedPairs,
    int TruncatedTexts,
    bool AlertsTruncated,
    IReadOnlyList<LocalQualityLeakagePair> PotentialLeaks);

public static class LocalQualityLeakageAudit
{
    private const int MaximumTextCharacters = 16_000;
    private const int MaximumAlerts = 100;
    private const double SimilarityThreshold = 0.65;

    private sealed record Fingerprint(LocalQualitySample Sample, string ExactDigest, HashSet<ulong> Shingles, ulong[] Minima);

    public static async Task<LocalQualityLeakageReport> AuditSnapshotAsync(LocalTextQualityLab lab, string snapshotHash,
        CancellationToken cancellationToken = default)
    {
        var development = new List<Fingerprint>();
        var holdout = new List<Fingerprint>();
        var truncated = 0;
        await foreach (var entry in lab.StreamSnapshotCasesAsync(snapshotHash, "development", cancellationToken))
            development.Add(CreateFingerprint(entry, ref truncated));
        await foreach (var entry in lab.StreamSnapshotCasesAsync(snapshotHash, "holdout", cancellationToken))
            holdout.Add(CreateFingerprint(entry, ref truncated));

        var alerts = new List<LocalQualityLeakagePair>();
        var compared = 0;
        var alertsTruncated = false;
        foreach (var left in development)
        foreach (var right in holdout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            compared++;
            if (left.Sample.SourceGroupSha256 == right.Sample.SourceGroupSha256)
                throw new InvalidDataException("A source group appears in both dataset splits.");
            var exact = left.ExactDigest == right.ExactDigest;
            var possibleNearMatch = left.Shingles.Count > 0 && right.Shingles.Count > 0
                && Math.Min(left.Shingles.Count, right.Shingles.Count) >= SimilarityThreshold * Math.Max(left.Shingles.Count, right.Shingles.Count)
                && left.Minima.Intersect(right.Minima).Any();
            if (!exact && !possibleNearMatch) continue;
            var similarity = exact ? 1 : Jaccard(left.Shingles, right.Shingles);
            if (similarity < SimilarityThreshold) continue;
            if (alerts.Count >= MaximumAlerts) { alertsTruncated = true; continue; }
            alerts.Add(new(left.Sample.SourceSha256, right.Sample.SourceSha256,
                left.Sample.SourceGroupSha256!, right.Sample.SourceGroupSha256!, Math.Round(similarity, 4),
                exact ? "normalized-template-match" : "near-text-match"));
        }
        return new(snapshotHash, development.Count, holdout.Count, compared, truncated, alertsTruncated, alerts);
    }

    private static Fingerprint CreateFingerprint(LocalQualityCase entry, ref int truncated)
    {
        var normalized = NormalizeTemplate(entry.ReferenceText);
        if (normalized.Length > MaximumTextCharacters)
        {
            normalized = normalized[..MaximumTextCharacters];
            truncated++;
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var shingles = new HashSet<ulong>();
        for (var index = 0; index <= normalized.Length - 5; index++)
        {
            ulong hash = 14695981039346656037;
            for (var offset = 0; offset < 5; offset++) hash = (hash ^ normalized[index + offset]) * 1099511628211;
            shingles.Add(hash);
        }
        return new(entry.Sample, digest, shingles, shingles.Order().Take(16).ToArray());
    }

    private static string NormalizeTemplate(string text)
    {
        var result = new StringBuilder(text.Length);
        var whitespace = false;
        var digit = false;
        foreach (var character in text.Normalize(NormalizationForm.FormC).ToLowerInvariant())
        {
            if (char.IsDigit(character))
            {
                if (!digit) result.Append('#');
                digit = true;
                whitespace = false;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (!whitespace) result.Append(' ');
                whitespace = true;
                digit = false;
            }
            else
            {
                result.Append(character);
                whitespace = false;
                digit = false;
            }
        }
        return result.ToString().Trim();
    }

    private static double Jaccard(HashSet<ulong> left, HashSet<ulong> right)
    {
        var shared = left.Count <= right.Count ? left.Count(right.Contains) : right.Count(left.Contains);
        return shared / (double)(left.Count + right.Count - shared);
    }
}

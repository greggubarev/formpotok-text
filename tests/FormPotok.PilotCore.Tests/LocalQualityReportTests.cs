using System.Text.Json;
using FormPotok.Ocr.Contracts;
using FormPotok.PilotCore.LocalText;

namespace FormPotok.PilotCore.Tests;

public sealed class LocalQualityReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "formpotok-quality-report-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Report_ComparesOnlyHoldoutAndDoesNotIncludeDocumentText()
    {
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        var holdout = await AddSampleAsync(workspace, lab, "holdout");
        var development = await AddSampleAsync(workspace, lab, "development");
        var predictions = new[]
        {
            new LocalQualityPrediction("candidate", holdout.SourceSha256, "Кот мир", 12, 128),
            new LocalQualityPrediction("candidate", development.SourceSha256, "Совсем неверно", 900, 4096),
            new LocalQualityPrediction("missing-candidate", development.SourceSha256, "Кот мир"),
        };
        var report = await LocalQualityEvaluator.EvaluateAsync(lab, predictions);

        Assert.Equal("holdout", report.Split);
        Assert.Equal(LocalQualityEvaluator.NormalizationPolicy, report.NormalizationPolicy);
        Assert.Equal(1, report.SourceDocuments);
        Assert.False(report.MinimumHoldoutFileCountMet);
        var candidate = Assert.Single(report.Candidates, item => item.Candidate == "candidate");
        Assert.True(candidate.Complete);
        Assert.Equal(1, candidate.Completed);
        Assert.Equal(0, candidate.Cer);
        Assert.Equal(0, candidate.Wer);
        Assert.Equal(12, candidate.P95DurationMilliseconds);
        Assert.Equal(128, candidate.P95PeakMemoryBytes);
        Assert.Single(candidate.Items);
        var baseline = Assert.Single(report.Candidates, item => item.Candidate == "current-at-review");
        Assert.Equal(1, baseline.Completed);
        Assert.True(baseline.Cer > 0);
        Assert.Equal(0.5, baseline.Wer);
        Assert.Null(baseline.P95DurationMilliseconds);
        var missing = Assert.Single(report.Candidates, item => item.Candidate == "missing-candidate");
        Assert.False(missing.Complete);
        Assert.Equal(1, missing.Missing);
        Assert.Null(missing.Cer);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("Кот мир", json);
        Assert.DoesNotContain("Кит мир", json);
        Assert.DoesNotContain(_root, json);
        Assert.Equal(report.DatasetSha256, (await LocalQualityEvaluator.EvaluateAsync(lab, predictions)).DatasetSha256);
    }

    [Fact]
    public async Task Report_RejectsDuplicatePredictionsAndTracksEmptyOrFailedCases()
    {
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        var sample = await AddSampleAsync(workspace, lab, "holdout");
        await Assert.ThrowsAsync<ArgumentException>(() => LocalQualityEvaluator.EvaluateAsync(lab,
            [new("candidate", sample.SourceSha256, "a"), new("candidate", sample.SourceSha256, "b")]));
        var report = await LocalQualityEvaluator.EvaluateAsync(lab,
            [new("empty", sample.SourceSha256, ""), new("failed", sample.SourceSha256, null, Error: "ocr-failure")]);
        Assert.Equal(1, Assert.Single(report.Candidates, item => item.Candidate == "empty").Empty);
        Assert.Equal(1, Assert.Single(report.Candidates, item => item.Candidate == "failed").Failed);
    }

    [Fact]
    public async Task Cer_CountsUnicodeScalarsRatherThanUtf16CodeUnits()
    {
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        await AddSampleAsync(workspace, lab, "holdout", "А😀", "А😃");
        var report = await LocalQualityEvaluator.EvaluateAsync(lab, []);
        var item = Assert.Single(Assert.Single(report.Candidates).Items);
        Assert.Equal(2, item.ReferenceCharacters);
        Assert.Equal(1, item.CharacterEdits);
        Assert.Equal(0.5, Assert.Single(report.Candidates).Cer);
    }

    private async Task<LocalQualitySample> AddSampleAsync(LocalTextWorkspace workspace, LocalTextQualityLab lab, string split,
        string reference = "Кот мир", string baseline = "Кит мир")
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var source = Path.Combine(_root, $"source-{split}-{attempt}.png");
            Directory.CreateDirectory(_root);
            await File.WriteAllTextAsync(source, "image bytes " + split + attempt);
            var document = await workspace.ImportAsync(source);
            if (LocalTextQualityLab.SplitForSource(document.SourceHash) != split) continue;
            var extraction = new ExtractionEnvelope("document", "test", "1", [
                new ExtractionPage(1, 100, 100, [new ExtractionTextBlock("line", baseline, 0.8, [])], []),
            ], [], [], []);
            var reviewed = document.WithExtraction(extraction).WithEdit(reference).Approve();
            return await lab.SaveReviewedAsync(reviewed, workspace.ResolveSourcePath(document));
        }
        throw new InvalidOperationException("Could not generate requested split.");
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}

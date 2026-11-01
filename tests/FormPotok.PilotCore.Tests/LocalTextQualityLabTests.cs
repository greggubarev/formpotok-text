using System.Text.Json;
using FormPotok.Ocr.Contracts;
using FormPotok.PilotCore.LocalText;

namespace FormPotok.PilotCore.Tests;

public sealed class LocalTextQualityLabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "formpotok-quality-lab-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveReviewed_CopiesSourceAndKeepsPreviousReferenceVersions()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.png");
        await File.WriteAllTextAsync(source, "image bytes");
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var document = (await workspace.ImportAsync(source)).WithExtraction(Envelope()).WithEdit("Исправлено").Approve();
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));

        var first = await lab.SaveReviewedAsync(document, workspace.ResolveSourcePath(document));
        Assert.Equal(1, first.Version);
        Assert.Equal(LocalTextQualityLab.SplitForSource(document.SourceHash), first.Split);
        Assert.Equal("Исправлено", await File.ReadAllTextAsync(Path.Combine(lab.Root, "references", first.SourceSha256, first.ReferenceSha256 + ".txt")));
        Assert.Equal("Исходный", await File.ReadAllTextAsync(Path.Combine(lab.Root, "baselines", first.SourceSha256, first.BaselineSha256 + ".txt")));
        Assert.Equal("image bytes", await File.ReadAllTextAsync(Path.Combine(lab.Root, "sources", first.SourceSha256 + ".png")));
        Assert.Equal(first, await lab.SaveReviewedAsync(document, workspace.ResolveSourcePath(document)));
        Assert.Equal(first, Assert.Single(await lab.ListSamplesAsync()));
        var streamed = new List<LocalQualityCase>();
        await foreach (var entry in lab.StreamCasesAsync(first.Split)) streamed.Add(entry);
        Assert.Equal("Исправлено", Assert.Single(streamed).ReferenceText);

        var revised = document.WithEdit("Исправлено точнее").Approve();
        var second = await lab.SaveReviewedAsync(revised, workspace.ResolveSourcePath(revised));
        Assert.Equal(2, second.Version);
        Assert.Equal(first.SourceSha256, second.SourceSha256);
        Assert.Equal(first.Split, second.Split);
        Assert.NotEqual(first.ReferenceSha256, second.ReferenceSha256);
        Assert.True(File.Exists(Path.Combine(lab.Root, "references", first.SourceSha256, first.ReferenceSha256 + ".txt")));
        var current = JsonSerializer.Deserialize<LocalQualitySample>(await File.ReadAllTextAsync(
            Path.Combine(lab.Root, "samples", first.SourceSha256 + ".json")), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(second, current);
    }

    [Fact]
    public async Task SaveReviewed_RejectsUnconfirmedTextAndChangedSource()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.png");
        await File.WriteAllTextAsync(source, "image bytes");
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var document = (await workspace.ImportAsync(source)).WithExtraction(Envelope());
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lab.SaveReviewedAsync(document, workspace.ResolveSourcePath(document)));
        await File.WriteAllTextAsync(workspace.ResolveSourcePath(document), "changed bytes");
        await Assert.ThrowsAsync<InvalidDataException>(() => lab.SaveReviewedAsync(document.Approve(), workspace.ResolveSourcePath(document)));
        Assert.False(Directory.Exists(lab.Root));
    }

    [Fact]
    public void Split_UsesOnlyValidatedSourceDigest()
    {
        var digest = new string('a', 64);
        Assert.Equal(LocalTextQualityLab.SplitForSource(digest), LocalTextQualityLab.SplitForSource(digest));
        Assert.Equal("holdout", LocalTextQualityLab.SplitForSource("00" + new string('a', 62)));
        Assert.Equal("development", LocalTextQualityLab.SplitForSource("01" + new string('a', 62)));
        Assert.Throws<ArgumentException>(() => LocalTextQualityLab.SplitForSource("../bad"));
    }

    [Fact]
    public async Task SaveReviewed_RejectsDamagedMetadataBeforeUsingItsPaths()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.png");
        await File.WriteAllTextAsync(source, "image bytes");
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var document = (await workspace.ImportAsync(source)).WithExtraction(Envelope()).Approve();
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        var sample = await lab.SaveReviewedAsync(document, workspace.ResolveSourcePath(document));
        var metadata = Path.Combine(lab.Root, "samples", sample.SourceSha256 + ".json");
        await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(sample with { SourceExtension = "../outside" }));

        await Assert.ThrowsAsync<InvalidDataException>(() => lab.SaveReviewedAsync(document, workspace.ResolveSourcePath(document)));
        Assert.False(Directory.Exists(Path.Combine(_root, "outside")));
    }

    [Fact]
    public async Task LoadCases_RejectsChangedReferenceBytes()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.png");
        await File.WriteAllTextAsync(source, "image bytes");
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var document = (await workspace.ImportAsync(source)).WithExtraction(Envelope()).Approve();
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        var sample = await lab.SaveReviewedAsync(document, workspace.ResolveSourcePath(document));
        var reference = Path.Combine(lab.Root, "references", sample.SourceSha256, sample.ReferenceSha256 + ".txt");
        await File.WriteAllTextAsync(reference, "tampered");

        await Assert.ThrowsAsync<InvalidDataException>(() => lab.LoadCasesAsync(sample.Split));
        await Assert.ThrowsAsync<InvalidDataException>(() => lab.SaveReviewedAsync(document, workspace.ResolveSourcePath(document)));
    }

    [Fact]
    public async Task Snapshot_KeepsGroupedSourcesAndReviewedVersionAfterLaterEdits()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        var sourceA = Path.Combine(_root, "a.png");
        var sourceB = Path.Combine(_root, "b.png");
        await File.WriteAllTextAsync(sourceA, "scan a");
        await File.WriteAllTextAsync(sourceB, "scan b");
        var a = (await workspace.ImportAsync(sourceA)).WithExtraction(Envelope()).WithEdit("Первый").Approve();
        var b = (await workspace.ImportAsync(sourceB)).WithExtraction(Envelope()).WithEdit("Второй").Approve();
        await lab.SaveReviewedAsync(a, workspace.ResolveSourcePath(a));
        await lab.SaveReviewedAsync(b, workspace.ResolveSourcePath(b));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lab.FreezeSnapshotAsync());

        var groupedA = await lab.AssignSourceGroupAsync(a.SourceHash, "Форма 7 / сканы");
        var groupedB = await lab.AssignSourceGroupAsync(b.SourceHash, " форма 7 / сканы ");
        Assert.Equal(groupedA.SourceGroupSha256, groupedB.SourceGroupSha256);
        Assert.Equal(groupedA.Split, groupedB.Split);
        Assert.Equal(groupedA, await lab.AssignSourceGroupAsync(a.SourceHash, "ФОРМА 7 / СКАНЫ"));

        var snapshot = await lab.FreezeSnapshotAsync();
        Assert.Equal(snapshot, await lab.FreezeSnapshotAsync());
        var before = new List<LocalQualityCase>();
        await foreach (var entry in lab.StreamSnapshotCasesAsync(snapshot, groupedA.Split)) before.Add(entry);
        Assert.Equal(2, before.Count);
        var report = await LocalQualityEvaluator.EvaluateSnapshotAsync(lab, snapshot, [], groupedA.Split);
        Assert.Equal(snapshot, report.SnapshotSha256);
        Assert.Equal(2, report.SourceDocuments);
        Assert.Equal(1, report.SourceGroups);
        Assert.False(report.MinimumHoldoutGroupCountMet);
        var revised = a.WithEdit("Первый исправлен").Approve();
        await lab.SaveReviewedAsync(revised, workspace.ResolveSourcePath(revised));
        var after = new List<LocalQualityCase>();
        await foreach (var entry in lab.StreamSnapshotCasesAsync(snapshot, groupedA.Split)) after.Add(entry);
        Assert.Equal(before, after);
        Assert.NotEqual(snapshot, await lab.FreezeSnapshotAsync());
        Assert.Equal("Первый", after.Single(item => item.Sample.SourceSha256 == a.SourceHash).ReferenceText);
    }

    [Fact]
    public async Task Snapshot_RejectsChangedManifestAndMissingReference()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.png");
        await File.WriteAllTextAsync(source, "image bytes");
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var document = (await workspace.ImportAsync(source)).WithExtraction(Envelope()).Approve();
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        var sample = await lab.SaveReviewedAsync(document, workspace.ResolveSourcePath(document));
        sample = await lab.AssignSourceGroupAsync(sample.SourceSha256, "batch-a");
        var snapshot = await lab.FreezeSnapshotAsync();
        var reference = Path.Combine(lab.Root, "references", sample.SourceSha256, sample.ReferenceSha256 + ".txt");
        await File.WriteAllTextAsync(reference, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in lab.StreamSnapshotCasesAsync(snapshot, sample.Split)) { }
        });
        var manifest = Path.Combine(lab.Root, "snapshots", snapshot + ".json");
        await File.WriteAllTextAsync(manifest, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in lab.StreamSnapshotCasesAsync(snapshot, sample.Split)) { }
        });
    }

    private static ExtractionEnvelope Envelope() => new("document", "test", "1", [
        new ExtractionPage(1, 100, 100, [new ExtractionTextBlock("line", "Исходный", 0.8, [])], []),
    ], [], [], []);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}

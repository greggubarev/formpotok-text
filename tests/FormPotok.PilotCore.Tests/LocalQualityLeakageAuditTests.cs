using FormPotok.Ocr.Contracts;
using FormPotok.PilotCore.LocalText;

namespace FormPotok.PilotCore.Tests;

public sealed class LocalQualityLeakageAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "formpotok-leakage-audit", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Audit_FlagsCrossSplitTemplateEvenWhenNumbersDiffer()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        await SaveAsync(workspace, lab, "scan-one", "Документ № 123 от 10.10.2026. Архив 42. Подпись проверена.", Group("development"));
        await SaveAsync(workspace, lab, "scan-two", "Документ № 987 от 11.10.2026. Архив 77. Подпись проверена.", Group("holdout"));

        var snapshot = await lab.FreezeSnapshotAsync();
        var audit = await LocalQualityLeakageAudit.AuditSnapshotAsync(lab, snapshot);

        Assert.Equal(snapshot, audit.SnapshotSha256);
        Assert.Equal(1, audit.ComparedPairs);
        var leak = Assert.Single(audit.PotentialLeaks);
        Assert.Equal("normalized-template-match", leak.Reason);
        Assert.Equal(1, leak.Similarity);
        Assert.False(audit.AlertsTruncated);
        Assert.Equal(0, audit.TruncatedTexts);
    }

    [Fact]
    public async Task Audit_DoesNotCompareVariantsInsideOneGroup()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        await SaveAsync(workspace, lab, "scan-one", "Акт № 123. Принято.", "shared-template");
        await SaveAsync(workspace, lab, "scan-two", "Акт № 124. Принято.", "shared-template");

        var audit = await LocalQualityLeakageAudit.AuditSnapshotAsync(lab, await lab.FreezeSnapshotAsync());

        Assert.Equal(0, audit.ComparedPairs);
        Assert.Empty(audit.PotentialLeaks);
    }

    [Fact]
    public async Task Audit_FlagsNearDuplicateWithOneChangedPhrase()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new LocalTextWorkspace(Path.Combine(_root, "workspace"));
        var lab = new LocalTextQualityLab(Path.Combine(_root, "lab"));
        var common = "Документ принят в архив. Проверены дата, номер, подпись и список приложений. ";
        await SaveAsync(workspace, lab, "scan-one", common + "Результат: принято без замечаний. " + common,
            Group("development"));
        await SaveAsync(workspace, lab, "scan-two", common + "Результат: требуется уточнение. " + common,
            Group("holdout"));

        var audit = await LocalQualityLeakageAudit.AuditSnapshotAsync(lab, await lab.FreezeSnapshotAsync());

        var leak = Assert.Single(audit.PotentialLeaks);
        Assert.Equal("near-text-match", leak.Reason);
        Assert.InRange(leak.Similarity, 0.65, 0.9999);
    }

    private async Task SaveAsync(LocalTextWorkspace workspace, LocalTextQualityLab lab, string sourceName,
        string reference, string group)
    {
        var source = Path.Combine(_root, sourceName + ".png");
        await File.WriteAllTextAsync(source, sourceName);
        var envelope = new ExtractionEnvelope("document", "test", "1", [
            new ExtractionPage(1, 100, 100, [new ExtractionTextBlock("line", "Исходный", 0.9, [])], [])
        ], [], [], []);
        var document = (await workspace.ImportAsync(source)).WithExtraction(envelope).WithEdit(reference).Approve();
        await lab.SaveReviewedAsync(document, workspace.ResolveSourcePath(document));
        await lab.AssignSourceGroupAsync(document.SourceHash, group);
    }

    private static string Group(string split)
    {
        for (var index = 0; index < 100; index++)
        {
            var group = "template-" + split + "-" + index;
            if (LocalTextQualityLab.SplitForSource(LocalTextQualityLab.HashSourceGroup(group)) == split) return group;
        }
        throw new InvalidOperationException("Could not find a source group for the requested split.");
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}

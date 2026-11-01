using System.Text.Json;
using System.Text.Json.Nodes;
using FormPotok.Ocr.Contracts;
using FormPotok.PilotCore.LocalText;

namespace FormPotok.PilotCore.Tests;

public sealed class LocalTextWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "formpotok-local-text-tests", Guid.NewGuid().ToString("N"));
    private LocalTextWorkspace CreateWorkspace() => new(Path.Combine(_root, "workspace"));

    [Fact]
    public async Task Import_CopiesSourcesAndDeduplicatesByBytesNotNames()
    {
        var first = await Source("first.png");
        var alias = await Source("alias.jpg");
        using var workspace = CreateWorkspace();
        var document = await workspace.ImportAsync(first);
        Assert.Equal(document.Id, (await workspace.ImportAsync(alias)).Id);
        Assert.Single(workspace.Documents);
        File.Delete(first);
        Assert.True(File.Exists(workspace.ResolveSourcePath(document)));
        Assert.Equal("first.png", document.DisplayName);
    }

    [Fact]
    public async Task Restart_PreservesExtractionGeometryAndEditedTextWithoutApprovalAfterEdit()
    {
        Guid id;
        using (var workspace = CreateWorkspace())
        {
            var document = (await workspace.ImportAsync(await Source("source.png"))).WithExtraction(Envelope());
            id = document.Id;
            workspace.Replace(document.Approve().WithEdit("Исправлено"));
            await workspace.SaveAsync();
        }
        using var reopened = CreateWorkspace();
        await reopened.LoadAsync();
        var restored = Assert.Single(reopened.Documents);
        Assert.Equal(id, restored.Id);
        Assert.Equal("Исходный текст", restored.OriginalText);
        Assert.Equal("Исправлено", restored.EditedText);
        Assert.Equal(LocalTextStatus.NeedsReview, restored.Status);
        Assert.Equal(new Point2D(1, 2), restored.Extraction!.Pages[0].TextBlocks[0].Polygon[0]);
    }

    [Fact]
    public async Task ReviewMarks_SurviveRestartAndDistinguishIdenticalBlockText()
    {
        using (var workspace = CreateWorkspace())
        {
            var document = (await workspace.ImportAsync(await Source("source.png"))).WithExtraction(ReviewEnvelope());
            Assert.Equal(2, document.RemainingUncertainCount());
            var reviewed = document.WithBlockReview(0, 0, true);
            Assert.False(document.IsBlockReviewed(0, 0));
            Assert.True(reviewed.IsBlockReviewed(0, 0));
            Assert.False(reviewed.IsBlockReviewed(0, 1));
            Assert.Equal(1, reviewed.RemainingUncertainCount());
            Assert.Equal([new LocalTextReviewMark(0, 0)], reviewed.ReviewedBlocks);
            workspace.Replace(reviewed);
            await workspace.SaveAsync();
        }
        using var reopened = CreateWorkspace();
        await reopened.LoadAsync();
        var restored = Assert.Single(reopened.Documents);
        Assert.Equal([new LocalTextReviewMark(0, 0)], restored.ReviewedBlocks);
        Assert.Equal(1, restored.RemainingUncertainCount());
        Assert.Equal(2, restored.WithBlockReview(0, 0, false).RemainingUncertainCount());
        Assert.Same(restored, restored.WithBlockReview(0, 0, true));
        Assert.Throws<NotSupportedException>(() => ((IList<LocalTextReviewMark>)restored.ReviewedBlocks!).Add(new(0, 1)));
    }

    [Fact]
    public void ReviewMarks_ResetForEveryNewExtractionAndRejectInvalidIndexes()
    {
        var document = new LocalTextDocument(Guid.NewGuid(), "source.png", new string('a', 64), "sources/" + new string('a', 64) + ".png");
        Assert.Equal(0, document.RemainingUncertainCount());
        Assert.Throws<InvalidOperationException>(() => document.WithBlockReview(0, 0, true));
        document = document.WithExtraction(ReviewEnvelope()).WithBlockReview(0, 1, true);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.WithBlockReview(-1, 0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.IsBlockReviewed(0, 3));
        Assert.Equal(1, document.WithBlockReview(0, 2, true).RemainingUncertainCount());
        var callerMarks = new List<LocalTextReviewMark> { new(0, 0) };
        var copied = document with { ReviewedBlocks = callerMarks };
        callerMarks.Clear();
        Assert.Equal([new LocalTextReviewMark(0, 0)], copied.ReviewedBlocks);
        Assert.Null(document.WithExtraction(ReviewEnvelope()).ReviewedBlocks);
        Assert.Equal(2, document.WithExtraction(ReviewEnvelope()).RemainingUncertainCount());
    }

    [Fact]
    public async Task LegacyWorkspaceWithoutReviewedBlocks_LoadsWithNoMarks()
    {
        using var workspace = CreateWorkspace();
        workspace.Replace((await workspace.ImportAsync(await Source("source.png"))).WithExtraction(ReviewEnvelope()));
        await workspace.SaveAsync();
        var state = Path.Combine(workspace.Root, "workspace.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(state))!;
        json["documents"]![0]!.AsObject().Remove("reviewedBlocks");
        await File.WriteAllTextAsync(state, json.ToJsonString());
        await workspace.LoadAsync();
        var restored = Assert.Single(workspace.Documents);
        Assert.Null(restored.ReviewedBlocks);
        Assert.Equal(2, restored.RemainingUncertainCount());
    }

    [Theory]
    [InlineData("[{\"pageIndex\":-1,\"blockIndex\":0}]")]
    [InlineData("[{\"pageIndex\":1,\"blockIndex\":0}]")]
    [InlineData("[{\"pageIndex\":0,\"blockIndex\":3}]")]
    [InlineData("[{\"pageIndex\":0,\"blockIndex\":0},{\"pageIndex\":0,\"blockIndex\":0}]")]
    [InlineData("[{}]")]
    [InlineData("[{\"pageIndex\":0}]")]
    [InlineData("[{\"pageIndex\":0,\"blockIndex\":\"0\"}]")]
    [InlineData("[null]")]
    public async Task InvalidPersistedReviewMarks_AreRejectedWithoutChangingState(string marks)
    {
        using var workspace = CreateWorkspace();
        workspace.Replace((await workspace.ImportAsync(await Source("source.png"))).WithExtraction(ReviewEnvelope()));
        await workspace.SaveAsync();
        var state = Path.Combine(workspace.Root, "workspace.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(state))!;
        json["documents"]![0]!["reviewedBlocks"] = JsonNode.Parse(marks);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(json);
        await File.WriteAllBytesAsync(state, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.LoadAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(state));
    }

    [Fact]
    public async Task PersistedMarksWithoutExtraction_AreRejected()
    {
        using var workspace = CreateWorkspace();
        var document = await workspace.ImportAsync(await Source("source.png"));
        var state = Path.Combine(workspace.Root, "workspace.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, documents = new[]
        {
            document with { ReviewedBlocks = [new LocalTextReviewMark(0, 0)] }
        } }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllBytesAsync(state, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.LoadAsync());
    }

    [Fact]
    public async Task ReviewRequired_ExportCannotSilentlyIncludeUnapprovedOrOverwriteSources()
    {
        using var workspace = CreateWorkspace();
        var document = (await workspace.ImportAsync(await Source("source.png"))).WithExtraction(Envelope());
        workspace.Replace(document);
        var output = Path.Combine(_root, "result.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ExportDocumentAsync(document.Id, output));
        workspace.Replace(document.Approve());
        await workspace.ExportDocumentAsync(document.Id, output);
        Assert.Equal("Исходный текст", await File.ReadAllTextAsync(output));
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.NotEqual(new byte[] { 239, 187, 191 }, bytes.Take(3).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ExportDocumentAsync(document.Id, workspace.ResolveSourcePath(document)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ExportCombinedAsync(Path.Combine(workspace.Root, "result.txt")));
        Assert.True(File.Exists(workspace.ResolveSourcePath(document)));
    }

    [Fact]
    public async Task CombinedExport_UsesOnlyReviewedTextInQueueOrder()
    {
        using var workspace = CreateWorkspace();
        var first = (await workspace.ImportAsync(await Source("one.png", "one"))).WithExtraction(Envelope()).WithEdit("Первый").Approve();
        var second = (await workspace.ImportAsync(await Source("two.png", "two"))).WithExtraction(Envelope());
        var third = (await workspace.ImportAsync(await Source("three.png", "three"))).WithExtraction(Envelope()).WithEdit("Третий").Approve();
        workspace.Replace(first); workspace.Replace(second); workspace.Replace(third);
        var output = Path.Combine(_root, "combined.txt");
        await workspace.ExportCombinedAsync(output);
        Assert.Equal("Первый\n\nТретий", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task ControlledCombinedExport_ReordersExcludesAndMatchesPreview()
    {
        using var workspace = CreateWorkspace();
        var first = (await workspace.ImportAsync(await Source("one.png", "one"))).WithExtraction(Envelope()).WithEdit("Первый").Approve();
        var second = (await workspace.ImportAsync(await Source("two.png", "two"))).WithExtraction(Envelope()).WithEdit("Второй").Approve();
        var third = (await workspace.ImportAsync(await Source("three.png", "three"))).WithExtraction(Envelope()).WithEdit("Третий").Approve();
        workspace.Replace(first); workspace.Replace(second); workspace.Replace(third);
        var ids = new[] { third.Id, first.Id };
        var preview = workspace.PreviewCombinedExport(ids, true);
        Assert.Equal("===== Документ 1: three.png =====\nТретий\n\n===== Документ 2: one.png =====\nПервый", preview);
        var output = Path.Combine(_root, "controlled.txt");
        await workspace.ExportCombinedAsync(output, ids, true);
        Assert.Equal(preview, await File.ReadAllTextAsync(output));
        Assert.DoesNotContain("Второй", preview);
        Assert.NotEqual(new byte[] { 239, 187, 191 }, (await File.ReadAllBytesAsync(output)).Take(3).ToArray());
        Assert.Equal("Третий\n\nПервый", workspace.PreviewCombinedExport(ids, false));
    }

    [Fact]
    public async Task ControlledCombinedExport_RejectsUnreviewedUnknownAndDuplicateWithoutReplacingFile()
    {
        using var workspace = CreateWorkspace();
        var reviewed = (await workspace.ImportAsync(await Source("one.png", "one"))).WithExtraction(Envelope()).Approve();
        var unreviewed = (await workspace.ImportAsync(await Source("two.png", "two"))).WithExtraction(Envelope());
        workspace.Replace(reviewed); workspace.Replace(unreviewed);
        var output = Path.Combine(_root, "existing.txt");
        await File.WriteAllTextAsync(output, "keep");
        foreach (var ids in new[] { Array.Empty<Guid>(), new[] { reviewed.Id, reviewed.Id }, new[] { unreviewed.Id }, new[] { Guid.NewGuid() } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ExportCombinedAsync(output, ids, true));
        Assert.Equal("keep", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task InterruptedRecognition_RestartsAsQueued()
    {
        using (var workspace = CreateWorkspace())
        {
            var document = await workspace.ImportAsync(await Source("source.png"));
            workspace.Replace(document with { Status = LocalTextStatus.Recognizing });
            await workspace.SaveAsync();
        }
        using var reopened = CreateWorkspace();
        await reopened.LoadAsync();
        Assert.Equal(LocalTextStatus.Queued, Assert.Single(reopened.Documents).Status);
    }

    [Fact]
    public async Task UnknownSchemaAndTraversal_AreRejectedWithoutOverwritingStoredState()
    {
        using var workspace = CreateWorkspace();
        var state = Path.Combine(workspace.Root, "workspace.json");
        const string unknown = "{\"schemaVersion\":99,\"documents\":[]}";
        await File.WriteAllTextAsync(state, unknown);
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.LoadAsync());
        Assert.Equal(unknown, await File.ReadAllTextAsync(state));
        var malicious = new LocalTextDocument(Guid.NewGuid(), "source.png", new string('a', 64), "sources/../../outside.png");
        Assert.Throws<InvalidDataException>(() => workspace.ResolveSourcePath(malicious));
    }

    [Fact]
    public async Task CancellationAndUnsupportedInput_DoNotLeaveDocumentsOrPartialSources()
    {
        using var workspace = CreateWorkspace();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await workspace.ImportAsync(await Source("source.exe")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.ImportAsync(Path.Combine(_root, "source.png"), new CancellationToken(true)));
        Assert.Empty(workspace.Documents);
        Assert.Empty(Directory.GetFiles(Path.Combine(workspace.Root, "sources")));
    }

    [Fact]
    public void Workspace_IsExclusiveAndDoesNotAcquireAnyAuthenticationSession()
    {
        using var workspace = CreateWorkspace();
        Assert.Throws<IOException>(() => CreateWorkspace());
        Assert.False(File.Exists(Path.Combine(workspace.Root, "auth-session.dat")));
    }

    [Fact]
    public async Task OversizedSource_IsRejectedBeforeCopyAndLeavesNoPartialSource()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "large.pdf");
        using (var source = File.Create(path)) source.SetLength(LocalTextWorkspace.MaximumSourceBytes + 1);
        using var workspace = CreateWorkspace();
        await Assert.ThrowsAsync<LocalTextSourceLimitException>(() => workspace.ImportAsync(path));
        Assert.Equal(20L * 1024 * 1024 + 1, new FileInfo(path).Length);
        Assert.Empty(workspace.Documents);
        Assert.Empty(Directory.GetFiles(Path.Combine(workspace.Root, "sources")));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "workspace.json")));
    }

    [Fact]
    public async Task FailureCode_SurvivesRestartAndClearsAfterSuccessfulRetry()
    {
        using (var workspace = CreateWorkspace())
        {
            var document = await workspace.ImportAsync(await Source("source.png"));
            workspace.Replace(document with { Status = LocalTextStatus.Failed, Failure = LocalTextFailure.ModelsMissing });
            await workspace.SaveAsync();
        }
        using var reopened = CreateWorkspace();
        await reopened.LoadAsync();
        var restored = Assert.Single(reopened.Documents);
        Assert.Equal(LocalTextFailure.ModelsMissing, restored.Failure);
        Assert.Equal(LocalTextFailure.None, restored.WithExtraction(Envelope()).Failure);
    }

    [Fact]
    public async Task UnknownFailureCode_PreservesStateAndFailsClosed()
    {
        using var workspace = CreateWorkspace();
        var document = await workspace.ImportAsync(await Source("source.png"));
        var state = Path.Combine(workspace.Root, "workspace.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, documents = new[] { document with { Failure = (LocalTextFailure)999 } } }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllBytesAsync(state, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.LoadAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(state));
    }

    [Fact]
    public async Task EmptyExtraction_RestoresRetryAndKeepsGeometryForManualReview()
    {
        Guid id;
        using (var workspace = CreateWorkspace())
        {
            var document = await workspace.ImportAsync(await Source("source.png"));
            id = document.Id;
            var empty = Envelope() with { Pages = [Envelope().Pages[0] with { TextBlocks = [] }] };
            document = document.WithExtraction(empty);
            Assert.Equal(LocalTextStatus.Failed, document.Status);
            Assert.Equal(LocalTextFailure.EmptyText, document.Failure);
            Assert.Equal(LocalTextStatus.Failed, document.WithEdit(" ").Status);
            Assert.Throws<InvalidOperationException>(() => document.Approve());
            workspace.Replace(document); await workspace.SaveAsync();
        }
        using var reopened = CreateWorkspace();
        await reopened.LoadAsync();
        var restored = Assert.Single(reopened.Documents);
        Assert.Equal(id, restored.Id);
        Assert.Equal(LocalTextStatus.Failed, restored.Status);
        Assert.NotNull(restored.Extraction);
        var edited = restored.WithEdit("Введено и проверено вручную");
        Assert.Equal(LocalTextFailure.None, edited.Failure);
        Assert.Equal(LocalTextStatus.Reviewed, edited.Approve().Status);
        Assert.Equal(LocalTextStatus.NeedsReview, restored.WithExtraction(Envelope()).Status);
    }

    [Fact]
    public async Task PreviousEmptyNeedsReviewState_IsRecoveredAsRetryableWithoutDroppingSource()
    {
        using var workspace = CreateWorkspace();
        var document = (await workspace.ImportAsync(await Source("source.png"))).WithExtraction(Envelope()).WithEdit(" ");
        document = document with { OriginalText = "", EditedText = "", Status = LocalTextStatus.NeedsReview };
        workspace.Replace(document); await workspace.SaveAsync();
        await workspace.LoadAsync();
        var recovered = Assert.Single(workspace.Documents);
        Assert.Equal(LocalTextStatus.Failed, recovered.Status);
        Assert.Equal(LocalTextFailure.EmptyText, recovered.Failure);
        Assert.True(File.Exists(workspace.ResolveSourcePath(recovered)));
    }

    [Fact]
    public void MissingTextOrInvalidPages_CannotBeApproved()
    {
        var document = new LocalTextDocument(Guid.NewGuid(), "file.png", new string('a', 64), "sources/file.png");
        Assert.Throws<InvalidOperationException>(() => document.Approve());
        Assert.Throws<InvalidDataException>(() => document.WithExtraction(Envelope() with { Pages = [] }));
        Assert.Throws<InvalidOperationException>(() => document.WithExtraction(Envelope()).WithEdit(" ").Approve());
    }

    internal static ExtractionEnvelope Envelope() => new("source", "local", "1", [
        new ExtractionPage(1, 100, 100, [new ExtractionTextBlock("1", "Исходный текст", 0.95, [new Point2D(1, 2)])], [])
    ], [], [], []);

    private static ExtractionEnvelope ReviewEnvelope() => Envelope() with { Pages = [new ExtractionPage(1, 100, 100,
        [new ExtractionTextBlock("1", "Same", 0.74, [new Point2D(1, 2)]),
         new ExtractionTextBlock("2", "Same", 0.50, [new Point2D(3, 4)]),
         new ExtractionTextBlock("3", "Certain", 0.75, [new Point2D(5, 6)])], [])] };

    [Theory]
    [InlineData("pages")]
    [InlineData("artifacts")]
    [InlineData("textBlocks")]
    public async Task InvalidNestedExtraction_IsRejectedWithoutOverwritingState(string member)
    {
        using var workspace = CreateWorkspace();
        var document = (await workspace.ImportAsync(await Source("source.png"))).WithExtraction(Envelope());
        var envelope = member switch
        {
            "pages" => document.Extraction! with { Pages = null! },
            "artifacts" => document.Extraction! with { Artifacts = null! },
            _ => document.Extraction! with { Pages = [document.Extraction!.Pages[0] with { TextBlocks = null! }] },
        };
        var state = Path.Combine(workspace.Root, "workspace.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, documents = new[] { document with { Extraction = envelope } } }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllBytesAsync(state, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.LoadAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(state));
    }

    private async Task<string> Source(string name, string text = "test-image-bytes")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}

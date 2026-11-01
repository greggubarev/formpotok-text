using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using FormPotok.Ocr.Contracts;
using FormPotok.Infrastructure.OcrRuntime;
using FormPotok.PilotCore.LocalText;

if (args.Length is 3 or 4 or 5 && args[0] == "--quality-report")
{
    var root = args[1];
    var output = Path.GetFullPath(args[2]);
    var labRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (output.StartsWith(labRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Report output must be outside laboratory source storage.");
    IReadOnlyList<LocalQualityPrediction> predictions = [];
    if (args.Length >= 4)
    {
        var input = Path.GetFullPath(args[3]);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Prediction input and report output must differ.");
        if (new FileInfo(input).Length > 32 * 1024 * 1024)
            throw new InvalidDataException("Prediction input exceeds the local size limit.");
        predictions = JsonSerializer.Deserialize<List<LocalQualityPrediction>>(await File.ReadAllTextAsync(input),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Prediction input is empty.");
        if (predictions.Count > 100_000 || predictions.Any(item => item is null))
            throw new InvalidDataException("Prediction input exceeds the entry limit.");
    }
    var lab = new LocalTextQualityLab(root);
    var qualityReport = args.Length == 5
        ? await LocalQualityEvaluator.EvaluateSnapshotAsync(lab, args[4], predictions)
        : await LocalQualityEvaluator.EvaluateAsync(lab, predictions);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(qualityReport,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(temporary, output, overwrite: true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
    Console.WriteLine(JsonSerializer.Serialize(new { qualityReport.SourceDocuments, qualityReport.MinimumHoldoutFileCountMet,
        candidates = qualityReport.Candidates.Select(candidate => candidate.Candidate).ToArray(), output }));
    return 0;
}

if (args is ["--self-test"])
{
    if (Distance("кот".ToCharArray(), "кит".ToCharArray()) != 1
        || Distance("текст".ToCharArray(), "тест".ToCharArray()) != 1
        || Distance(Array.Empty<char>(), "я".ToCharArray()) != 1
        || Distance(new[] { "первый", "лист" }, new[] { "второй", "лист" }) != 1
        || Normalize("  ТЕКСТ\r\n\tЛист  ") != "текст лист"
        || Normalize("ёж") == Normalize("еж"))
        throw new InvalidDataException("Comparison self-test failed.");
    var referenceChecks = TestSyntheticReferences();
    var argumentChecks = TestSmokeArguments();
    Console.WriteLine(JsonSerializer.Serialize(new { passed = true, comparisonChecks = 6, referenceChecks, argumentChecks }));
    return 0;
}

if (!TryParseSmokeArguments(args, out var disableTextDocumentUnwarping, out var retryMemoryWithoutUnwarping,
        out var forceTextOriginalVariant, out var tileTextPage, out var referenceManifest))
{
    Console.Error.WriteLine("Usage: LocalTextSmoke <repository-root> <source-file> <isolated-output-root> <model-cache-root> [synthetic-manifest] [--disable-text-document-unwarping] [--force-text-original-variant] [--tile-text-page] [--retry-memory-without-unwarping]");
    return 2;
}

var repository = Path.GetFullPath(args[0]);
var source = Path.GetFullPath(args[1]);
var runRoot = Path.GetFullPath(args[2]);
var sidecar = Path.Combine(repository, "tools", "ocr-sidecar");
var storage = Path.Combine(runRoot, "workspace");
var modelCache = Path.GetFullPath(args[3]);
var sourceSidecarScript = Environment.GetEnvironmentVariable("FORMPOTOK_LAB_SIDECAR_SOURCE");
var sourcePython = Environment.GetEnvironmentVariable("FORMPOTOK_LAB_PYTHON");
if ((sourceSidecarScript is null) != (sourcePython is null)
    || sourceSidecarScript is not null && (!Path.IsPathFullyQualified(sourceSidecarScript)
        || !Path.IsPathFullyQualified(sourcePython!) || !File.Exists(sourceSidecarScript) || !File.Exists(sourcePython!)))
    throw new InvalidOperationException("Laboratory source sidecar requires existing absolute script and Python paths.");
var recognizerModel = Environment.GetEnvironmentVariable("FORMPOTOK_LAB_RECOGNIZER") switch
{
    null or "" or "eslav" => "eslav_PP-OCRv5_mobile_rec",
    "cyrillic" => "cyrillic_PP-OCRv5_mobile_rec",
    _ => throw new InvalidOperationException("Unknown laboratory recognizer model.")
};
var reference = referenceManifest is not null
    ? ParseSyntheticReference(await File.ReadAllTextAsync(referenceManifest), Path.GetFileName(source)) : null;
if (Directory.Exists(storage)) throw new InvalidOperationException("Smoke requires a fresh isolated workspace.");
await using var sourceStream = File.OpenRead(source);
if (sourceStream.Length is <= 0 or > LocalTextWorkspace.MaximumSourceBytes)
    throw new InvalidDataException("Source byte length is outside the smoke budget.");
var sourceBefore = await SHA256.HashDataAsync(sourceStream);
if (reference is not null)
    ValidateSourceIdentity(reference, sourceStream.Length, Convert.ToHexStringLower(sourceBefore));
var modelNames = new[] { "PP-OCRv5_mobile_det", recognizerModel, "PP-LCNet_x1_0_doc_ori", "UVDoc", "PP-LCNet_x1_0_textline_ori" };
var inventory = modelNames.SelectMany(name => new[] { "inference.json", "inference.yml", "inference.pdiparams" }
    .Select(file => $"{name}/{file}:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(modelCache, "official_models", name, file))))}"));
var modelDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', inventory))));
var timer = Stopwatch.StartNew();
Guid documentId;
int pages;
int blocks;
int characters;
object? comparison = null;
using (var workspace = new LocalTextWorkspace(storage))
{
    var document = await workspace.ImportAsync(source);
    documentId = document.Id;
    var client = new LocalOcrRuntimeClient(new LocalOcrRuntimeOptions
    {
        SidecarExecutablePath = sourceSidecarScript is null && File.Exists(Path.Combine(sidecar, "runtime", "formpotok-ocr-sidecar.exe"))
            ? Path.Combine(sidecar, "runtime", "formpotok-ocr-sidecar.exe") : null,
        PythonExecutablePath = sourcePython ?? Path.Combine(sidecar, ".venv", "Scripts", "python.exe"),
        SidecarScriptPath = sourceSidecarScript ?? Path.Combine(sidecar, "main.py"),
        WorkingDirectory = sourceSidecarScript is null ? sidecar : Path.GetDirectoryName(sourceSidecarScript)!,
        OutputDirectory = Path.Combine(storage, "ocr-runs"),
        AutoBootstrapPythonEnvironment = false,
        TextOnly = true,
        OfflineOnly = true,
        DisableTextDocumentUnwarping = disableTextDocumentUnwarping,
        RetryTextWithoutUnwarpingOnMemoryLimit = retryMemoryWithoutUnwarping,
        ForceTextOriginalVariant = forceTextOriginalVariant,
        TileTextPage = tileTextPage,
        PreferredEngine = "paddle",
        PaddleModelCacheHome = modelCache,
        PaddleDetectionModelDirectory = Path.Combine(modelCache, "official_models", "PP-OCRv5_mobile_det"),
        PaddleRecognitionModelDirectory = Path.Combine(modelCache, "official_models", recognizerModel),
        PaddleRecognitionModelName = recognizerModel,
        MaximumPages = 20,
        MaximumPagePixels = 20_000_000,
        MaximumProcessMemoryBytes = 2L * 1024 * 1024 * 1024,
        ExtractTimeout = TimeSpan.FromSeconds(300),
        TessdataDirectory = Path.Combine(sidecar, "tessdata"),
    });
    var extraction = await client.ExtractAsync(workspace.ResolveSourcePath(document));
    if (extraction.Candidates.Count != 0) throw new InvalidDataException("Text mode returned transport candidates.");
    document = document.WithExtraction(extraction);
    if (string.IsNullOrWhiteSpace(document.OriginalText)) throw new InvalidDataException("Recognition produced no text.");
    if (reference is not null) comparison = CompareSyntheticReference(reference, extraction);
    workspace.Replace(document);
    var export = Path.Combine(runRoot, "reviewed.txt");
    try { await workspace.ExportDocumentAsync(documentId, export); throw new InvalidOperationException("Review gate did not block export."); }
    catch (InvalidOperationException error) when (error.Message == "Review approval is required.") { }
    document = document.WithEdit(document.EditedText + "\nПроверено.").Approve();
    workspace.Replace(document);
    await workspace.SaveAsync();
    await workspace.ExportDocumentAsync(documentId, export);
    if (await File.ReadAllTextAsync(export) != document.EditedText) throw new InvalidDataException("Export differs from the approved text.");
    pages = extraction.Pages.Count;
    blocks = extraction.Pages.Sum(page => page.TextBlocks.Count);
    characters = document.OriginalText.Length;
}
using (var reopened = new LocalTextWorkspace(storage))
{
    await reopened.LoadAsync();
    var restored = reopened.Documents.Single();
    if (restored.Id != documentId || restored.Status != LocalTextStatus.Reviewed || restored.Extraction is null
        || !restored.EditedText.EndsWith("Проверено.", StringComparison.Ordinal))
        throw new InvalidDataException("Review or extraction was not preserved across restart.");
}
sourceStream.Position = 0;
var sourceAfter = await SHA256.HashDataAsync(sourceStream);
if (!sourceBefore.SequenceEqual(sourceAfter)) throw new InvalidDataException("Source was changed.");
var report = new { passed = true, scope = "local-workflow-smoke",
    recognitionQualityVerdict = reference is null ? "unreviewed-diagnostic-not-promotion" : "synthetic-diagnostic-not-promotion",
    recognizerModel,
    sidecarMode = sourceSidecarScript is null ? "packaged" : "source-diagnostic",
    publicReleaseAccepted = false, sourceSha256 = Convert.ToHexStringLower(sourceBefore), modelDigest, pages, blocks, characters,
    durationSeconds = Math.Round(timer.Elapsed.TotalSeconds, 2), offlineOnly = true, osNetworkSandbox = false,
    documentUnwarpingPolicy = disableTextDocumentUnwarping ? "disabled-text-offline-diagnostic"
        : retryMemoryWithoutUnwarping ? "memory-fallback-offline-diagnostic" : "baseline-default",
    workerCommitLimitBytes = 2L * 1024 * 1024 * 1024, transportCandidates = 0, sourceUnchanged = true,
    reviewGatePassed = true, restartPassed = true, comparison };
Directory.CreateDirectory(runRoot);
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(runRoot, "smoke-summary.json"), json);
Console.WriteLine(json);
return 0;

static bool TryParseSmokeArguments(string[] arguments, out bool disableTextDocumentUnwarping,
    out bool retryMemoryWithoutUnwarping, out bool forceTextOriginalVariant,
    out bool tileTextPage, out string? referenceManifest)
{
    disableTextDocumentUnwarping = false;
    retryMemoryWithoutUnwarping = false;
    forceTextOriginalVariant = false;
    tileTextPage = false;
    referenceManifest = null;
    if (arguments.Length < 4 || arguments.Length > 8 || arguments.Take(4).Any(string.IsNullOrWhiteSpace))
        return false;
    foreach (var value in arguments.Skip(4))
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (value)
        {
            case "--disable-text-document-unwarping" when !disableTextDocumentUnwarping:
                disableTextDocumentUnwarping = true;
                break;
            case "--retry-memory-without-unwarping" when !retryMemoryWithoutUnwarping:
                retryMemoryWithoutUnwarping = true;
                break;
            case "--force-text-original-variant" when !forceTextOriginalVariant:
                forceTextOriginalVariant = true;
                break;
            case "--tile-text-page" when !tileTextPage:
                tileTextPage = true;
                break;
            default:
                if (value.StartsWith('-') || referenceManifest is not null
                    || disableTextDocumentUnwarping || retryMemoryWithoutUnwarping
                    || forceTextOriginalVariant || tileTextPage)
                    return false;
                referenceManifest = value;
                break;
        }
    }
    return (!forceTextOriginalVariant || disableTextDocumentUnwarping)
        && (!tileTextPage || forceTextOriginalVariant)
        && (!retryMemoryWithoutUnwarping || (!disableTextDocumentUnwarping && !forceTextOriginalVariant
                                             && !tileTextPage));
}

static int TestSmokeArguments()
{
    string[] baseline = ["repository", "source", "output", "models"];
    string[] manifest = [.. baseline, "manifest.json"];
    const string flag = "--disable-text-document-unwarping";
    const string retry = "--retry-memory-without-unwarping";
    const string original = "--force-text-original-variant";
    const string tile = "--tile-text-page";
    (string[] Arguments, bool Valid, bool Disabled, bool Retry, bool Original, bool Tile, string? Manifest)[] cases = [
        (baseline, true, false, false, false, false, null),
        (manifest, true, false, false, false, false, "manifest.json"),
        ([.. baseline, flag], true, true, false, false, false, null),
        ([.. manifest, flag], true, true, false, false, false, "manifest.json"),
        ([.. baseline, retry], true, false, true, false, false, null),
        ([.. manifest, retry], true, false, true, false, false, "manifest.json"),
        ([.. baseline, flag, original], true, true, false, true, false, null),
        ([.. manifest, flag, original], true, true, false, true, false, "manifest.json"),
        ([.. baseline, flag, original, tile], true, true, false, true, true, null),
        ([.. manifest, flag, original, tile], true, true, false, true, true, "manifest.json"),
        ([.. baseline, tile], false, false, false, false, true, null),
        ([.. baseline, original], false, false, false, true, false, null),
        ([.. baseline, retry, original], false, false, true, true, false, null),
        ([.. baseline, " "], false, false, false, false, false, null),
        ([.. manifest, "--unknown"], false, false, false, false, false, "manifest.json"),
        ([.. baseline, " ", flag], false, true, false, false, false, null),
        ([.. baseline, flag, flag], false, true, false, false, false, null),
        ([.. baseline, retry, retry], false, false, true, false, false, null),
        ([.. manifest, flag, "extra"], false, true, false, false, false, "manifest.json"),
        (baseline[..3], false, false, false, false, false, null),
    ];
    foreach (var test in cases)
        if (TryParseSmokeArguments(test.Arguments, out var disabled, out var enabledRetry,
                out var enabledOriginal, out var enabledTile, out var manifestPath) != test.Valid
            || (test.Valid && (disabled != test.Disabled || enabledRetry != test.Retry
                || enabledOriginal != test.Original || enabledTile != test.Tile
                || manifestPath != test.Manifest)))
            throw new InvalidOperationException("Smoke argument self-test failed.");
    return cases.Length;
}

static string Normalize(string text) => string.Join(' ', text.Normalize(NormalizationForm.FormKC)
    .ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

static JsonElement RequiredProperty(JsonElement owner, string name, JsonValueKind kind)
{
    if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var value) || value.ValueKind != kind)
        throw new InvalidDataException($"Synthetic manifest has an invalid {name}.");
    return value;
}

static long RequiredInteger(JsonElement owner, string name, long minimum, long maximum)
{
    var value = RequiredProperty(owner, name, JsonValueKind.Number);
    if (!value.TryGetInt64(out var number) || number < minimum || number > maximum)
        throw new InvalidDataException($"Synthetic manifest has an invalid {name}.");
    return number;
}

static string RequiredText(JsonElement owner, string name)
{
    var value = RequiredProperty(owner, name, JsonValueKind.String).GetString()!;
    if (string.IsNullOrWhiteSpace(value) || Normalize(value).Length == 0)
        throw new InvalidDataException($"Synthetic manifest has an empty {name}.");
    return value;
}

static SyntheticReference ParseSyntheticReference(string json, string sourceFile)
{
    try
    {
        using var manifest = JsonDocument.Parse(json);
        var root = manifest.RootElement;
        RequiredInteger(root, "schemaVersion", 1, 1);
        if (RequiredText(root, "purpose") != "diagnostic-only"
            || RequiredText(root, "dataPartition") != "synthetic-diagnostic")
            throw new InvalidDataException("Smoke requires a synthetic diagnostic manifest.");
        RequiredProperty(root, "countsTowardRealHoldoutQuota", JsonValueKind.False);
        RequiredProperty(root, "countsTowardTrainingQuota", JsonValueKind.False);
        var sources = RequiredProperty(root, "sources", JsonValueKind.Array);
        if (sources.GetArrayLength() == 0) throw new InvalidDataException("Synthetic manifest has no sources.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SyntheticReference? selected = null;
        foreach (var entry in sources.EnumerateArray())
        {
            var filename = RequiredText(entry, "file");
            var extension = Path.GetExtension(filename).ToLowerInvariant();
            if (filename != Path.GetFileName(filename) || filename.IndexOfAny(['/', '\\']) >= 0
                || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !names.Add(filename)
                || extension is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
                throw new InvalidDataException("Synthetic manifest has an invalid or duplicate source filename.");
            var hash = RequiredText(entry, "sha256");
            if (hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Synthetic manifest has an invalid source SHA-256.");
            var bytes = RequiredInteger(entry, "byteLength", 1, LocalTextWorkspace.MaximumSourceBytes);
            var count = (int)RequiredInteger(entry, "pageCount", 1, extension == ".pdf" ? 20 : 1);
            var pages = RequiredProperty(entry, "pages", JsonValueKind.Array);
            if (pages.GetArrayLength() != count)
                throw new InvalidDataException("Synthetic reference does not cover every declared page.");
            var references = new List<SyntheticPageReference>(count);
            foreach (var page in pages.EnumerateArray())
            {
                var number = references.Count + 1;
                RequiredInteger(page, "pageNumber", number, number);
                var text = RequiredText(page, "expectedText");
                var marker = RequiredText(page, "marker");
                if (!text.Contains(marker, StringComparison.Ordinal)
                    || !Normalize(text).Contains(Normalize(marker), StringComparison.Ordinal))
                    throw new InvalidDataException("Synthetic marker is absent from its expected page text.");
                references.Add(new(number, text, marker));
            }
            if (string.Equals(filename, sourceFile, StringComparison.OrdinalIgnoreCase))
                selected = new(filename, hash, bytes, references);
        }
        return selected ?? throw new InvalidDataException("Source filename is absent from the synthetic manifest.");
    }
    catch (JsonException error)
    {
        throw new InvalidDataException("Synthetic manifest JSON is invalid or truncated.", error);
    }
}

static void ValidateSourceIdentity(SyntheticReference reference, long byteLength, string sha256)
{
    if (reference.ByteLength != byteLength || reference.Sha256 != sha256)
        throw new InvalidDataException("Source byte length or SHA-256 differs from the synthetic manifest.");
}

static object CompareSyntheticReference(SyntheticReference reference, ExtractionEnvelope extraction)
{
    if (reference.Pages.Count == 0 || extraction.Pages is null || extraction.Pages.Count != reference.Pages.Count
        || extraction.Pages.Any(page => page is null)
        || !extraction.Pages.Select(page => page.PageNumber).Order().SequenceEqual(Enumerable.Range(1, reference.Pages.Count)))
        throw new InvalidDataException("Prediction does not cover every synthetic reference page exactly once.");
    var perPage = reference.Pages.Select(expected =>
    {
        var page = extraction.Pages.Single(item => item.PageNumber == expected.PageNumber);
        var text = Normalize(expected.ExpectedText);
        var prediction = Normalize(string.Join("\n", page.TextBlocks.Select(block => block.Text)));
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new { page = expected.PageNumber, referenceCharacters = text.Length,
            characterEdits = Distance(text.ToCharArray(), prediction.ToCharArray()), referenceWords = words.Length,
            wordEdits = Distance(words, prediction.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            markerPresent = prediction.Contains(Normalize(expected.Marker), StringComparison.Ordinal) };
    }).ToArray();
    var characters = perPage.Sum(page => (long)page.referenceCharacters);
    var words = perPage.Sum(page => (long)page.referenceWords);
    if (characters == 0 || words == 0) throw new InvalidDataException("Synthetic comparison denominator is empty.");
    return new { purpose = "diagnostic-only", dataPartition = "synthetic-diagnostic", countsTowardRealHoldoutQuota = false,
        cer = perPage.Sum(page => (long)page.characterEdits) / (double)characters,
        wer = perPage.Sum(page => (long)page.wordEdits) / (double)words, perPage };
}

static int TestSyntheticReferences()
{
    var hash = new string('a', 64);
    JsonObject Valid() => JsonSerializer.SerializeToNode(new {
        schemaVersion = 1, purpose = "diagnostic-only", dataPartition = "synthetic-diagnostic",
        countsTowardRealHoldoutQuota = false, countsTowardTrainingQuota = false,
        sources = new[] { new { file = "source.pdf", sha256 = hash, byteLength = 12, pageCount = 2,
            pages = new[] { new { pageNumber = 1, expectedText = "FIRST page", marker = "FIRST" },
                new { pageNumber = 2, expectedText = "SECOND page", marker = "SECOND" } } } }
    })!.AsObject();
    JsonObject Source(JsonObject root) => root["sources"]![0]!.AsObject();
    JsonArray Pages(JsonObject root) => Source(root)["pages"]!.AsArray();
    SyntheticReference Read(JsonObject root) => ParseSyntheticReference(root.ToJsonString(), "source.pdf");
    var checks = 0;
    void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { checks++; return; }
        throw new InvalidOperationException("Invalid synthetic reference was accepted.");
    }
    var valid = Read(Valid());
    ValidateSourceIdentity(valid, 12, hash);
    if (valid.Pages.Count != 2) throw new InvalidOperationException("Valid reference lost a page.");
    checks++;
    Action<JsonObject>[] invalid = [
        root => root["schemaVersion"] = 2,
        root => root["countsTowardTrainingQuota"] = true,
        root => root.Remove("countsTowardRealHoldoutQuota"),
        root => root["sources"] = null,
        root => root["sources"] = new JsonArray(),
        root => root["sources"]![0] = null,
        root => root["sources"]!.AsArray().Add(Source(root).DeepClone()),
        root => Source(root)["file"] = null,
        root => Source(root)["file"] = "../source.pdf",
        root => Source(root)["file"] = "other.pdf",
        root => Source(root)["sha256"] = null,
        root => Source(root)["sha256"] = new string('a', 63),
        root => Source(root)["sha256"] = new string('z', 64),
        root => Source(root)["byteLength"] = null,
        root => Source(root)["byteLength"] = 0,
        root => Source(root)["byteLength"] = LocalTextWorkspace.MaximumSourceBytes + 1,
        root => Source(root)["byteLength"] = "12",
        root => Source(root)["pageCount"] = null,
        root => Source(root)["pageCount"] = 0,
        root => Source(root)["pageCount"] = 1,
        root => Source(root)["pageCount"] = 21,
        root => Source(root).Remove("pages"),
        root => Source(root)["pages"] = null,
        root => Source(root)["pages"] = new JsonArray(),
        root => Pages(root).RemoveAt(1),
        root => Pages(root).Add(Pages(root)[1]!.DeepClone()),
        root => Pages(root)[0] = null,
        root => Pages(root)[1]!["pageNumber"] = 1,
        root => { Pages(root)[0]!["pageNumber"] = 2; Pages(root)[1]!["pageNumber"] = 1; },
        root => Pages(root)[0]!.AsObject().Remove("pageNumber"),
        root => Pages(root)[0]!["expectedText"] = null,
        root => Pages(root)[0]!["expectedText"] = "",
        root => Pages(root)[0]!["expectedText"] = " \t\n",
        root => Pages(root)[0]!["marker"] = null,
        root => Pages(root)[0]!["marker"] = "",
        root => Pages(root)[0]!["marker"] = " ",
        root => Pages(root)[0]!["marker"] = "ABSENT",
    ];
    foreach (var mutate in invalid)
    {
        var root = Valid(); mutate(root); Reject(() => Read(root));
    }
    Reject(() => ParseSyntheticReference("null", "source.pdf"));
    Reject(() => ParseSyntheticReference("", "source.pdf"));
    Reject(() => ParseSyntheticReference("[]", "source.pdf"));
    Reject(() => ParseSyntheticReference("{", "source.pdf"));
    Reject(() => ValidateSourceIdentity(valid, 13, hash));
    Reject(() => ValidateSourceIdentity(valid, 12, new string('b', 64)));
    ExtractionPage Page(int number, string text) => new(number, 100, 100,
        [new ExtractionTextBlock("1", text, 1, [])], []);
    ExtractionEnvelope Prediction(params ExtractionPage[] pages) => new("source", "local", "1", pages, [], [], []);
    Reject(() => CompareSyntheticReference(valid, Prediction()));
    Reject(() => CompareSyntheticReference(valid, Prediction() with { Pages = null! }));
    Reject(() => CompareSyntheticReference(valid, Prediction(Page(1, "FIRST page"))));
    Reject(() => CompareSyntheticReference(valid, Prediction(Page(1, "FIRST page"), Page(1, "FIRST page"))));
    Reject(() => CompareSyntheticReference(valid, Prediction(Page(1, "FIRST page"), Page(3, "SECOND page"))));
    Reject(() => CompareSyntheticReference(valid with { Pages = [new(1, "", ""), new(2, " ", " ")] },
        Prediction(Page(1, "FIRST page"), Page(2, "SECOND page"))));
    var complete = JsonSerializer.SerializeToElement(CompareSyntheticReference(valid,
        Prediction(Page(2, "SECOND page"), Page(1, "FIRST page"))));
    if (complete.GetProperty("cer").GetDouble() != 0 || complete.GetProperty("wer").GetDouble() != 0
        || complete.GetProperty("perPage").GetArrayLength() != 2)
        throw new InvalidOperationException("Complete prediction was not compared against every reference page.");
    checks++;
    var imperfect = JsonSerializer.SerializeToElement(CompareSyntheticReference(valid,
        Prediction(Page(1, "other text"), Page(2, ""))));
    if (imperfect.GetProperty("perPage")[0].GetProperty("markerPresent").GetBoolean()
        || imperfect.GetProperty("cer").GetDouble() <= 0 || imperfect.GetProperty("wer").GetDouble() <= 0)
        throw new InvalidOperationException("Missing text or marker did not remain a diagnostic comparison.");
    checks++;
    return checks;
}

static int Distance<T>(T[] expected, T[] actual)
{
    var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
    var current = new int[previous.Length];
    for (var row = 1; row <= expected.Length; row++)
    {
        current[0] = row;
        for (var column = 1; column <= actual.Length; column++)
            current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1),
                previous[column - 1] + (EqualityComparer<T>.Default.Equals(expected[row - 1], actual[column - 1]) ? 0 : 1));
        (previous, current) = (current, previous);
    }
    return previous[^1];
}

sealed record SyntheticReference(string File, string Sha256, long ByteLength, IReadOnlyList<SyntheticPageReference> Pages);
sealed record SyntheticPageReference(int PageNumber, string ExpectedText, string Marker);

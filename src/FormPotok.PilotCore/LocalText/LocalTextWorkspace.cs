using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FormPotok.PilotCore.LocalText;

public sealed class LocalTextWorkspace : IDisposable
{
    public const long MaximumSourceBytes = 20 * 1024 * 1024;
    public const int MaximumDocuments = 500;
    private const long MaximumStateBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg" };
    private readonly string _statePath;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _writer = new(1, 1);

    public LocalTextWorkspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "sources"));
        _statePath = Path.Combine(Root, "workspace.json");
        _lease = new FileStream(Path.Combine(Root, "workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public string Root { get; }
    public List<LocalTextDocument> Documents { get; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_statePath)) return;
        if (new FileInfo(_statePath).Length > MaximumStateBytes) throw new InvalidDataException("Workspace exceeds the size limit.");
        await using var stream = File.OpenRead(_statePath);
        Snapshot snapshot;
        try
        {
            snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Workspace is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Workspace JSON is invalid.", exception);
        }
        if (snapshot.SchemaVersion != 1 || snapshot.Documents is null || snapshot.Documents.Count > MaximumDocuments)
            throw new InvalidDataException("Unsupported workspace schema.");
        var ids = new HashSet<Guid>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in snapshot.Documents)
        {
            if (document is null || document.Id == Guid.Empty || document.SourceHash is null || document.SourceFile is null
                || string.IsNullOrWhiteSpace(document.DisplayName) || !ids.Add(document.Id) || !hashes.Add(document.SourceHash)
                || !Enum.IsDefined(document.Status) || !Enum.IsDefined(document.Failure) || document.DisplayName != Path.GetFileName(document.DisplayName)
                || document.OriginalText is null || document.EditedText is null)
                throw new InvalidDataException("Workspace document is invalid.");
            ResolveSourcePath(document);
            if ((document.Status is LocalTextStatus.NeedsReview or LocalTextStatus.Reviewed) && document.Extraction is null)
                throw new InvalidDataException("Reviewed document is missing its source extraction.");
            if (document.Extraction is not null) document.WithExtraction(document.Extraction);
            document.ValidateReviewedBlocks();
            if (document.Status == LocalTextStatus.Reviewed && string.IsNullOrWhiteSpace(document.EditedText))
                throw new InvalidDataException("Reviewed text is empty.");
        }
        Documents.Clear();
        Documents.AddRange(snapshot.Documents.Select(document => document.Status switch
        {
            LocalTextStatus.Recognizing => document with { Status = LocalTextStatus.Queued },
            LocalTextStatus.NeedsReview when string.IsNullOrWhiteSpace(document.OriginalText) && string.IsNullOrWhiteSpace(document.EditedText)
                => document with { Status = LocalTextStatus.Failed, Failure = LocalTextFailure.EmptyText },
            _ => document,
        }));
    }

    public async Task<LocalTextDocument> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(extension)) throw new InvalidDataException("Unsupported source format.");
        var temporary = Path.Combine(Root, "sources", $".{Guid.NewGuid():N}.tmp");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                if (source.Length > MaximumSourceBytes) throw new LocalTextSourceLimitException();
                var buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > MaximumSourceBytes) throw new LocalTextSourceLimitException();
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (total == 0) throw new InvalidDataException("Source is empty.");
            }
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            var existing = Documents.FirstOrDefault(document => document.SourceHash == digest);
            if (existing is not null) return existing;
            if (Documents.Count >= MaximumDocuments) throw new InvalidDataException("Workspace document limit reached.");
            var relative = $"sources/{digest}{extension}";
            File.Move(temporary, Path.Combine(Root, relative), overwrite: true);
            var document = new LocalTextDocument(Guid.NewGuid(), Path.GetFileName(path), digest, relative);
            Documents.Add(document);
            await SaveAsync(cancellationToken);
            return document;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string ResolveSourcePath(LocalTextDocument document)
    {
        if (document.SourceHash.Length != 64 || document.SourceHash.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Invalid source hash.");
        var extension = Path.GetExtension(document.SourceFile);
        var expected = $"sources/{document.SourceHash}{extension}";
        if (!Extensions.Contains(extension) || document.SourceFile != expected)
            throw new InvalidDataException("Invalid managed source path.");
        return Path.Combine(Root, expected);
    }

    public void Replace(LocalTextDocument document)
    {
        var index = Documents.FindIndex(item => item.Id == document.Id);
        if (index < 0) throw new InvalidOperationException("Document is not in this workspace.");
        Documents[index] = document;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(1, Documents.ToArray()), JsonOptions);
            if (bytes.Length > MaximumStateBytes) throw new InvalidDataException("Workspace exceeds the size limit.");
            await WriteAtomicallyAsync(_statePath, bytes, cancellationToken);
        }
        finally { _writer.Release(); }
    }

    public async Task ExportCombinedAsync(string target, CancellationToken cancellationToken = default)
    {
        var ids = Documents.Where(document => document.Status == LocalTextStatus.Reviewed).Select(document => document.Id).ToArray();
        await ExportCombinedAsync(target, ids, false, cancellationToken);
    }

    public string PreviewCombinedExport(IReadOnlyList<Guid> ids, bool includeHeaders)
    {
        if (ids.Count == 0 || ids.Count != ids.Distinct().Count())
            throw new InvalidOperationException("Select distinct reviewed documents.");
        var selected = ids.Select(id => Documents.SingleOrDefault(document => document.Id == id)
            ?? throw new InvalidOperationException("Document is not in this workspace.")).ToArray();
        if (selected.Any(document => document.Status != LocalTextStatus.Reviewed))
            throw new InvalidOperationException("Review approval is required.");
        if (!includeHeaders) return string.Join("\n\n", selected.Select(document => document.EditedText));
        return string.Join("\n\n", selected.Select((document, index) =>
            $"===== Документ {index + 1}: {SanitizeExportName(document.DisplayName)} =====\n{document.EditedText}"));
    }

    public async Task ExportCombinedAsync(string target, IReadOnlyList<Guid> ids, bool includeHeaders, CancellationToken cancellationToken = default)
    {
        var text = PreviewCombinedExport(ids, includeHeaders);
        var absolute = ValidateExportTarget(target);
        await WriteAtomicallyAsync(absolute, new UTF8Encoding(false).GetBytes(text), cancellationToken);
    }

    private static string SanitizeExportName(string name) => new(name.Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    public async Task ExportDocumentAsync(Guid id, string target, CancellationToken cancellationToken = default)
    {
        var document = Documents.Single(item => item.Id == id);
        if (document.Status != LocalTextStatus.Reviewed) throw new InvalidOperationException("Review approval is required.");
        var absolute = ValidateExportTarget(target);
        await WriteAtomicallyAsync(absolute, new UTF8Encoding(false).GetBytes(document.EditedText), cancellationToken);
    }

    private string ValidateExportTarget(string target)
    {
        var absolute = Path.GetFullPath(target);
        if (!string.Equals(Path.GetExtension(absolute), ".txt", StringComparison.OrdinalIgnoreCase)
            || absolute.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(absolute, Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TXT export must be outside workspace storage.");
        return absolute;
    }

    private static async Task WriteAtomicallyAsync(string target, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() { _lease.Dispose(); _writer.Dispose(); }
    private sealed record Snapshot(int SchemaVersion, IReadOnlyList<LocalTextDocument> Documents);
}

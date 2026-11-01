using System.Text.Json;
using FormPotok.Ocr.Contracts;
using FormPotok.PilotCore.LocalText;

namespace FormPotok.PilotCore.Tests;

public sealed class LocalTextProducerContractTests
{
    [Fact]
    public void PythonTextOnlyEnvelope_PreservesRussianTextAndGeometryInConsumer()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "local-text-extraction-envelope.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", "local-text-extraction-envelope.json"));
        var envelope = JsonSerializer.Deserialize<ExtractionEnvelope>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(envelope);
        Assert.Empty(envelope.Candidates);
        Assert.Equal(2, envelope.Pages.Count);
        Assert.Equal("original-pixels", envelope.Pages[0].CoordinateFrame);
        var document = new LocalTextDocument(Guid.NewGuid(), "sample.png", new string('a', 64), "sources/sample.png").WithExtraction(envelope);
        Assert.Contains("Заявление на выдачу справки", document.EditedText);
        Assert.Same(envelope, document.Extraction);
        Assert.Equal(LocalTextStatus.NeedsReview, document.Status);
    }
}

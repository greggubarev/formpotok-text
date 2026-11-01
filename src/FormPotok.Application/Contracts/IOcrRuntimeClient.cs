using FormPotok.Ocr.Contracts;

namespace FormPotok.Application.Contracts;

public interface IOcrRuntimeClient
{
    Task<OcrRuntimeHealth> HealthcheckAsync(CancellationToken cancellationToken = default);

    Task<ExtractionEnvelope> ExtractAsync(string inputPath, CancellationToken cancellationToken = default);
}

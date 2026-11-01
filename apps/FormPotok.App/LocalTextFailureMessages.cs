using FormPotok.Infrastructure.OcrRuntime;
using FormPotok.PilotCore.LocalText;

namespace FormPotok.App;

internal static class LocalTextFailureMessages
{
    public static LocalTextFailure Classify(Exception error) => error switch
    {
        LocalTextSourceLimitException => LocalTextFailure.SourceLimit,
        TimeoutException => LocalTextFailure.Timeout,
        LocalOcrRuntimeException runtime => runtime.ErrorCode switch
        {
            "OFFLINE_MODELS_MISSING" => LocalTextFailure.ModelsMissing,
            "TEXT_ONLY_SOURCE_LIMIT" => LocalTextFailure.SourceLimit,
            "TEXT_ONLY_PAGE_LIMIT" => LocalTextFailure.PageLimit,
            "TEXT_ONLY_PIXEL_LIMIT" => LocalTextFailure.PixelLimit,
            "TEXT_ONLY_SOURCE_INVALID" or "TEXT_ONLY_SOURCE_MISSING" or "TEXT_ONLY_EMPTY_DOCUMENT" or "TEXT_ONLY_UNSUPPORTED_SOURCE" => LocalTextFailure.InvalidSource,
            "TEXT_ONLY_RUNTIME_MISSING" => LocalTextFailure.RuntimeMissing,
            "TEXT_ONLY_MEMORY_LIMIT" => LocalTextFailure.MemoryLimit,
            "TEXT_ONLY_TIMEOUT" => LocalTextFailure.Timeout,
            "TEXT_ONLY_RESOURCE_GUARD_FAILED" => LocalTextFailure.ResourceGuard,
            "TEXT_ONLY_OUTPUT_LIMIT" => LocalTextFailure.OutputLimit,
            _ => LocalTextFailure.Unknown,
        },
        _ => LocalTextFailure.Unknown,
    };

    public static string Describe(LocalTextFailure failure) => failure switch
    {
        LocalTextFailure.ModelsMissing => "Не удалось распознать: локальные модели отсутствуют или неполны. Установите полный комплект программы. Документы и правки сохранены; скачивание моделей не выполнялось.",
        LocalTextFailure.RuntimeMissing => "Не удалось распознать: локальный модуль отсутствует. Установите полный комплект программы. Устанавливать Python отдельно не нужно; документы и правки сохранены.",
        LocalTextFailure.SourceLimit => "Не удалось добавить или обработать файл: размер больше 20 МиБ. Выберите меньший файл. Исходник не изменён.",
        LocalTextFailure.PageLimit => "Не удалось распознать: в PDF больше 20 страниц. Разделите документ на части и добавьте их отдельно.",
        LocalTextFailure.PixelLimit => "Не удалось распознать: страница содержит больше 20 миллионов пикселей. Уменьшите разрешение копии документа и добавьте её отдельно.",
        LocalTextFailure.MemoryLimit => "Не удалось распознать: превышен лимит памяти локального модуля. Разделите документ или уменьшите разрешение копии. Очередь сохранена; можно повторить обработку.",
        LocalTextFailure.Timeout => "Не удалось распознать за 5 минут. Разделите документ или уменьшите разрешение копии. Очередь сохранена; можно повторить обработку.",
        LocalTextFailure.ResourceGuard => "Не удалось безопасно запустить локальный модуль. Перезапустите программу; если ошибка повторяется, установите полный комплект. Документы и правки сохранены.",
        LocalTextFailure.OutputLimit => "Не удалось обработать слишком большой результат распознавания. Разделите документ на части. Очередь сохранена.",
        LocalTextFailure.EmptyText => "Текст не найден. Проверьте исходник: изображение должно быть читаемым. Можно повторить распознавание или ввести текст вручную и проверить его перед сохранением.",
        LocalTextFailure.InvalidSource => "Не удалось прочитать документ. Проверьте, открывается ли он в программе просмотра и не защищён ли PDF паролем. Исходник не изменён.",
        _ => "Не удалось распознать документ. Проверьте исходник и локальный комплект программы. Очередь и правки сохранены.",
    };
}

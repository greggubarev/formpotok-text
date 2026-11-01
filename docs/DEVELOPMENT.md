# Разработка

## Состав

- `apps/FormPotok.App`: интерфейс.
- `src/FormPotok.TextCore`: локальное хранилище и запуск OCR.
- `src/FormPotok.Ocr.Contracts`: формат результата.
- `tools/ocr-sidecar`: OCR и тесты.

Не добавляйте в репозиторий пользовательские документы, ключи, модельный кэш или результаты распознавания.

## Сборка исходников

На Windows x64 установите .NET SDK 10. Затем:

```powershell
dotnet build apps/FormPotok.App/FormPotok.App.csproj -c Release
dotnet test tests/FormPotok.PilotCore.Tests/FormPotok.PilotCore.Tests.csproj -c Release
```

Для тестов sidecar нужен Python 3.10 с зависимостями из `tools/ocr-sidecar/requirements-cpu.txt`:

```powershell
py -3.10 -m unittest discover -s tools/ocr-sidecar -p 'test_*.py' -q
```

`dotnet run --project apps/FormPotok.App -c Release` открывает интерфейс. Для распознавания подготовьте `tools/ocr-sidecar/runtime/formpotok-ocr-sidecar.exe` и `tools/ocr-sidecar/models/paddlex/official_models`. Локальный интерфейс не скачивает недостающие модели сам.

## Проверка

Тесты проверяют формат результата, очередь, настройки конфиденциальности и ошибки OCR:

```powershell
dotnet test tests/FormPotok.App.UiSmoke.Tests/FormPotok.App.UiSmoke.Tests.csproj -c Release
dotnet run --project tools/LocalTextSmoke/LocalTextSmoke.csproj -c Release -- --self-test
```

Для автоматического сравнения OCR с контрольным текстом распакуйте Windows-архив и выполните из корня репозитория:

```powershell
$package = 'C:\FormPotok.Text-win-x64-v0.1.0-preview.15'
$result = Join-Path $env:TEMP ([guid]::NewGuid().ToString())
dotnet run --project tools/LocalTextSmoke/LocalTextSmoke.csproj -c Release -- $package (Join-Path $package 'sample-letter.png') $result (Join-Path $package 'tools/ocr-sidecar/models/paddlex') examples/sample-letter.reference.json
```

Укажите в `$package` фактический путь к распакованной папке. Метрики записываются в `smoke-summary.json` внутри `$result`.

Для полного переносимого архива подготовьте sidecar скриптом `scripts/build-ocr-sidecar-runtime.ps1` либо проверьте ранее собранный runtime через `scripts/build-windows-portable.ps1 -RuntimeDirectory <каталог runtime> -ModelCacheDirectory <каталог моделей> -ValidateRuntimeOnly`. Затем выполните тот же скрипт без `-ValidateRuntimeOnly` для сборки пакета. `scripts/verify-windows-portable.ps1 -Archive <архив>` проверяет файлы архива и их контрольные суммы.

Для установщика используйте `scripts/build-windows-installer.ps1 -PortableArchive <архив>` и установленный Inno Setup 6. Скрипт проверяет ZIP и распаковывает его во временную папку сам.

Встроенные модели зафиксированы в [model lock](MODEL_PROVENANCE.md). Перед сборкой пакета скрипт `scripts/verify-model-lock.ps1` сверяет ревизии, заявленную лицензию, размеры и SHA-256 весов. Негативные проверки: `./tests/Test-ModelLock.ps1`.

Для Windows runtime версии Python-пакетов закреплены в `packaging/win-x64-runtime-dependencies.lock`, а SHA-256 выбранных колёс для Windows x64/Python 3.10 в `packaging/win-x64-cp310-wheels.lock`. Сборка устанавливает их с `--require-hashes --only-binary=:all:`, сверяет прямые требования, `pip check` и итоговый список пакетов; упаковка сверяет оба lock с манифестом runtime. Для полностью офлайн-сборки скачайте колёса в отдельный каталог через `pip download --only-binary=:all: --no-deps -r packaging/win-x64-runtime-dependencies.lock -d <каталог>` и передайте его как `-WheelhouseDirectory <каталог>`. Каталог колёс не входит в Git и пользовательский пакет. Негативные проверки: `./tests/Test-RuntimeDependencyLock.ps1` и `py -3.10 -m unittest tests/test_generate_wheel_lock.py -q`.

Для проверки точных ZIP и установщика на отдельной Windows x64 соберите автономный проверяющий набор:

```powershell
./scripts/build-windows-smoke-kit.ps1 -OutputRoot output/windows-smoke-kit
```

Перенесите его ZIP вместе с проверяемыми ZIP и установщиком на чистую машину. Распакуйте набор и запустите штатным Windows PowerShell; .NET SDK и Python на принимающей машине не нужны:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test-windows-artifacts.ps1 -PortableArchive C:\test\FormPotok.Text-win-x64.zip -Installer C:\test\FormPotok.Text-Setup.exe -OutputRoot C:\test\result
```

Скрипт проверяет хеши ZIP, распознаёт учебные PNG, JPEG и PDF из переносимой и установленной копии, затем удаляет установленную копию. Итог лежит в `result\artifact-smoke-summary.json`. Этот прогон сам по себе не доказывает отсутствие SDK/Python или сетевого доступа: проводите его в подготовленной офлайн-Windows и фиксируйте свойства среды отдельно. Используйте новый пустой `OutputRoot` для каждого прогона.

Для кандидата на выпуск используйте [проектную процедуру Windows release](../.agents/skills/formpotok-windows-release/SKILL.md). Она отделяет проверку целостности ZIP от запуска на чистой Windows x64. Пакетный smoke проверяет модели, зависимости и запуск OCR после установки, но не заменяет ручную проверку интерфейса, независимую офлайн-машину и аудит лицензий.

Номер версии и порядок выпуска описаны в [политике версионирования](VERSIONING.md). `scripts/check-version.ps1` проверяет `VERSION`; для публикации используйте `-RequireTag` после создания аннотированного тега.

## Локальное сравнение качества

После проверки документа в программе нажмите «Сохранить в эталон». Данные лежат только в `%LOCALAPPDATA%\FormPotok\Text\quality-lab`; текущий эталон выбирается по хешу источника. Для запуска доверенных локальных исполняемых файлов создайте `candidates.json` вне репозитория:

```json
{ "timeoutSeconds": 300, "candidates": [{ "name": "local-v1", "executable": "C:\\path\\to\\ocr.exe", "arguments": ["{source}"] }] }
```

Исполняемый файл должен напечатать распознанный текст в UTF-8 в стандартный вывод. Прогонщик передаёт ему копию источника, ограничивает время и размер вывода, но не изолирует сеть или доступ к диску: используйте только доверенные локальные программы. Результат `predictions.json` содержит распознанный текст и требует той же защиты, что исходные документы.

```powershell
$lab = Join-Path $env:LOCALAPPDATA 'FormPotok\Text\quality-lab'
$predictions = Join-Path $env:TEMP 'formpotok-predictions.json'
$report = Join-Path $env:TEMP 'formpotok-quality-report.json'
dotnet run --project tools/LocalQualityRunner/LocalQualityRunner.csproj -c Release -- run $lab $predictions (Join-Path $env:TEMP 'candidates.json')
dotnet run --project tools/LocalTextSmoke/LocalTextSmoke.csproj -c Release -- --quality-report $lab $report $predictions
```

Для воспроизводимого сравнения назначьте каждому эталону группу происхождения: одинаковый код группы нужен для пересканов одного документа и вариантов одного шаблона. Хеши источников находятся в именах файлов каталога `samples`. Название группы не сохраняется, хранится только его хеш; не используйте в названии личные данные. Разбиение `holdout/development` после назначения определяется группой.

```powershell
$sourceHash = '<хеш источника из имени файла samples\<хеш>.json>'
dotnet run --project tools/LocalQualityRunner/LocalQualityRunner.csproj -c Release -- group $lab $sourceHash 'template-07'
$snapshot = (dotnet run --project tools/LocalQualityRunner/LocalQualityRunner.csproj -c Release -- freeze $lab).Trim()
dotnet run --project tools/LocalQualityRunner/LocalQualityRunner.csproj -c Release -- run $lab $predictions (Join-Path $env:TEMP 'candidates.json') $snapshot
dotnet run --project tools/LocalTextSmoke/LocalTextSmoke.csproj -c Release -- --quality-report $lab $report $predictions $snapshot
dotnet run --project tools/LocalQualityRunner/LocalQualityRunner.csproj -c Release -- audit $lab $snapshot (Join-Path $env:TEMP 'formpotok-leakage-audit.json')
```

Команда `freeze` требует группы у всех эталонов. Снимок фиксирует версии, хеши текстов и разделы, поэтому последующие правки не изменяют старое сравнение. Назначение групп ручное: лаборатория пока не находит похожие документы автоматически и не подтверждает независимость групп. `sourceGroups` и `minimumHoldoutGroupCountMet` в отчёте показывают число групп и порог 30, но сами по себе не закрывают gate независимой проверки.

Команда `audit` ищет совпадения и похожий текст между разделами по сохранённому снимку. Её отчёт не содержит текста, но остаётся эвристикой: чистый результат не доказывает отсутствие общего источника. Для воспроизводимого прогона синтетических русскоязычных страниц используйте [локальную диагностическую лабораторию](../tools/quality-lab/README.md).

Если результаты получены другим способом, их можно передать в том же JSON-формате:

```json
[
  { "candidate": "candidate-a", "sourceSha256": "<64-символьный хеш источника>", "text": "Распознанный текст", "durationMilliseconds": 1200, "peakMemoryBytes": 250000000 }
]
```

Без аргумента с предсказаниями сравнивается только результат, сохранённый при проверке. Отчёт содержит хеши и метрики, но не тексты или пути. CER/WER считаются по политике `nfc-lower-invariant-collapse-whitespace-runes-v1`: регистр и повторные пробелы приводятся, `ё` и пунктуация сохраняются, CER считает Unicode-символы. Некорректные, отсутствующие и пустые результаты учитываются отдельно; для неполного набора итоговые CER/WER и p95 не выводятся. p95 памяти остаётся пустым, пока внешний прогонщик не передаёт надёжное измерение. `minimumHoldoutFileCountMet` означает лишь минимум 30 файлов; без назначенных групп разделение по хешу файла не доказывает независимость документов или шаблонов. Критерии принятия описаны в [протоколе качества](QUALITY_PROTOCOL.md).

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FormPotok.Application.Contracts;
using FormPotok.Infrastructure.OcrRuntime;
using FormPotok.Ocr.Contracts;
using FormPotok.PilotCore.Learning;
using FormPotok.PilotCore.LocalText;

namespace FormPotok.App;

public sealed class LocalTextWindow : Window
{
    private readonly LocalTextWorkspace _workspace;
    private readonly IOcrRuntimeClient _runtime;
    private readonly LocalLearningConsentStore _consentStore;
    private readonly LocalUsageStatisticsOutbox _statistics;
    private readonly LocalTextQualityLab _qualityLab;
    private LocalLearningConsent _consent = new();
    private readonly ListBox _queue = new();
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly TextBox _find = new() { PlaceholderText = "Найти в тексте" };
    private readonly Button _findPrevious = new() { Content = "↑", Width = 30, Height = 30 };
    private readonly Button _findNext = new() { Content = "↓", Width = 30, Height = 30 };
    private readonly TextBlock _findCount = new() { Text = "0", VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Grid _previewLayer = new();
    private readonly Canvas _highlightLayer = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly ListBox _reviewBlocks = new() { Height = 128 };
    private readonly CheckBox _uncertainOnly = new() { Content = "Только сомнительные", FontSize = 12 };
    private readonly Button _nextUncertain = new() { Content = "Следующая сомнительная" };
    private readonly CheckBox _blockReviewed = new() { Content = "Строка проверена", FontSize = 12 };
    private readonly TextBlock _uncertainCount = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _reviewEmpty = new() { Text = "Нет строк с низкой уверенностью.", FontSize = 12, IsVisible = false };
    private readonly Expander _reviewExpander = new() { Header = "Строки на странице", IsExpanded = false, IsVisible = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Expander _importFailures = new() { Header = "Не удалось добавить", IsExpanded = true, IsVisible = false };
    private readonly ListBox _importFailureList = new() { Height = 110 };
    private readonly TextBlock _progressLabel = new() { FontSize = 12 };
    private readonly ProgressBar _queueProgress = new() { Minimum = 0, Height = 6 };
    private readonly ProgressBar _ocrActivity = new() { IsIndeterminate = true, Height = 3 };
    private readonly StackPanel _recognitionProgress = new() { Spacing = 4, IsVisible = false };
    private readonly TextBlock _pageStatus = new();
    private readonly Button _add = new() { Content = "Добавить файлы" };
    private readonly Button _recognize = new() { Content = "Распознать очередь" };
    private readonly Button _cancel = new() { Content = "Отменить" };
    private readonly Button _approve = new() { Content = "Подтвердить текст" };
    private readonly Button _saveReference = new() { Content = "Сохранить в эталон" };
    private readonly Button _quality = new() { Content = "Качество" };
    private readonly Button _export = new() { Content = "Сохранить TXT" };
    private readonly Button _exportAll = new() { Content = "Общий TXT" };
    private readonly Button _previousPage = new() { Content = "←", Width = 36 };
    private readonly Button _nextPage = new() { Content = "→", Width = 36 };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Guid? _selectedId;
    private int _pageIndex;
    private bool _updatingEditor;
    private bool _busy;
    private bool _healthy;
    private bool _closing;
    private bool _allowClose;
    private Bitmap? _bitmap;
    private Avalonia.PixelSize _previewSourceSize;
    private (int Index, ExtractionTextBlock Block)[] _visibleBlocks = [];
    private ExtractionTextBlock? _highlightedBlock;
    private bool _updatingReviewBlocks;
    private bool _updatingReviewMark;
    private Guid? _pendingApprovalId;
    private CancellationTokenSource? _cancellation;
    private Task _activeOperation = Task.CompletedTask;
    private (int Start, int Length)[] _searchMatches = [];
    private int _searchIndex = -1;

    internal static Window CreateStartupWindow(Func<LocalTextWorkspace> workspaceFactory)
    {
        LocalTextWorkspace? workspace = null;
        try
        {
            workspace = workspaceFactory();
            return new LocalTextWindow(workspace, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            workspace?.Dispose();
            return new Window
            {
                Title = "ФормПоток: Текст", Width = 460, Height = 200, CanResize = false,
                Icon = BrandAssets.CreateWindowIcon(),
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new TextBlock
                {
                    Text = "Не удалось открыть локальное хранилище. Если программа уже запущена, вернитесь в её окно. Иначе проверьте доступ к папке и свободное место. Существующие документы не изменены.",
                    TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(24),
                },
            };
        }
    }

    public LocalTextWindow() : this(new LocalTextWorkspace(LocalTextRuntime.ResolveStorageRoot()), null) { }

    internal LocalTextWindow(LocalTextWorkspace workspace, IOcrRuntimeClient? runtime, bool initializeOnOpen = true)
    {
        _workspace = workspace;
        _runtime = runtime ?? new LocalOcrRuntimeClient(LocalTextRuntime.CreateOptions(workspace.Root));
        _consentStore = new LocalLearningConsentStore(workspace.Root);
        _statistics = new LocalUsageStatisticsOutbox(workspace.Root);
        _qualityLab = new LocalTextQualityLab(Path.Combine(workspace.Root, "quality-lab"));
        Title = "ФормПоток: Текст";
        Icon = BrandAssets.CreateWindowIcon();
        Width = 1180; Height = 760; MinWidth = 840; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        Content = BuildLayout();
        _queue.ItemTemplate = new FuncDataTemplate<LocalTextDocument>((document, _) => new StackPanel
        {
            Spacing = 3,
            Children = { new TextBlock { Text = document?.DisplayName, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = document is null ? "" : StatusLabel(document.Status), FontSize = 12, Foreground = Brushes.DimGray } },
        });
        _queue.SelectionChanged += (_, _) => SelectDocument();
        _editor.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty) return;
            if (_updatingEditor || Selected is not { Extraction: not null } selected) return;
            _workspace.Replace(selected.WithEdit(_editor.Text ?? ""));
            _pendingApprovalId = null;
            _saveTimer.Stop(); _saveTimer.Start();
            RefreshTextSearch();
            RefreshCommands();
        };
        _find.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty) RefreshTextSearch();
        };
        _findPrevious.Click += (_, _) => NavigateSearch(-1);
        _findNext.Click += (_, _) => NavigateSearch(1);
        _find.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            NavigateSearch(args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1, focusEditor: false);
            args.Handled = true;
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.F || !args.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            _find.Focus(); _find.SelectAll(); args.Handled = true;
        };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveEditsAsync(); };
        _add.Click += async (_, _) => await PickFilesAsync();
        _recognize.Click += async (_, _) => await RecognizeQueueAsync();
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _approve.Click += async (_, _) => await ApproveSelectedAsync();
        _saveReference.Click += async (_, _) => await SaveReferenceSelectedAsync();
        _quality.Click += async (_, _) => await (await CreateQualitySummaryAsync()).ShowDialog(this);
        _export.Click += async (_, _) => await PickExportAsync(false);
        _exportAll.Click += async (_, _) => await PickExportAsync(true);
        _previousPage.Click += (_, _) => { _pageIndex--; RefreshPreview(); };
        _nextPage.Click += (_, _) => { _pageIndex++; RefreshPreview(); };
        _reviewBlocks.SelectionChanged += (_, _) => SelectReviewBlock();
        _nextUncertain.Click += (_, _) => NavigateUncertain();
        _blockReviewed.PropertyChanged += async (_, args) =>
        {
            if (args.Property != CheckBox.IsCheckedProperty || _updatingReviewMark || Selected is not { } selected
                || _reviewBlocks.SelectedIndex < 0 || _reviewBlocks.SelectedIndex >= _visibleBlocks.Length) return;
            var blockIndex = _visibleBlocks[_reviewBlocks.SelectedIndex].Index;
            _workspace.Replace(selected.WithBlockReview(_pageIndex, blockIndex, _blockReviewed.IsChecked == true));
            _pendingApprovalId = null;
            RefreshReviewBlocks(blockIndex);
            RefreshCommands();
            await SaveEditsAsync();
        };
        _uncertainOnly.PropertyChanged += (_, args) =>
        {
            if (args.Property == CheckBox.IsCheckedProperty) RefreshReviewBlocks();
        };
        _highlightLayer.SizeChanged += (_, _) => RefreshHighlight();
        if (initializeOnOpen) Opened += async (_, _) => await InitializeAsync();
        Closing += async (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            await FlushAndCloseAsync();
        };
        Closed += (_, _) => { _saveTimer.Stop(); _bitmap?.Dispose(); _workspace.Dispose(); _cancellation?.Dispose(); };
        RefreshCommands();
    }

    private LocalTextDocument? Selected => _workspace.Documents.FirstOrDefault(document => document.Id == _selectedId);

    internal async Task FlushAndCloseAsync()
    {
        if (_closing) return;
        _closing = true;
        RefreshCommands();
        _cancellation?.Cancel();
        await _activeOperation;
        if (!await SaveEditsAsync()) { _closing = false; RefreshCommands(); return; }
        _allowClose = true;
        Close();
    }

    private Control BuildLayout()
    {
        SetId(_queue, "TextDocumentQueue", "Документы");
        SetId(_editor, "TextEditor", "Распознанный текст");
        SetId(_find, "TextFind", "Найти в тексте");
        SetId(_findPrevious, "TextFindPrevious", "Предыдущее совпадение");
        SetId(_findNext, "TextFindNext", "Следующее совпадение");
        SetId(_findCount, "TextFindCount", "Число совпадений");
        ToolTip.SetTip(_findPrevious, "Предыдущее совпадение");
        ToolTip.SetTip(_findNext, "Следующее совпадение");
        SetId(_status, "TextStatus", "Состояние обработки");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        SetId(_importFailures, "TextImportFailures", "Ошибки добавления файлов");
        SetId(_importFailureList, "TextImportFailureList", "Файлы, которые не удалось добавить");
        SetId(_progressLabel, "TextProgressLabel", "Обработано документов");
        SetId(_queueProgress, "TextQueueProgress", "Прогресс очереди");
        SetId(_ocrActivity, "TextOcrActivity", "Обработка текущего документа");
        SetId(_recognitionProgress, "TextRecognitionProgress", "Ход распознавания");
        SetId(_add, "TextAddFiles", "Добавить файлы");
        SetId(_recognize, "TextRecognize", "Распознать очередь");
        SetId(_cancel, "TextCancel", "Отменить распознавание");
        SetId(_approve, "TextApprove", "Подтвердить текст");
        SetId(_saveReference, "TextSaveReference", "Сохранить проверенный документ в локальный эталон");
        SetId(_quality, "TextQuality", "Предварительная диагностика локального распознавания");
        ToolTip.SetTip(_quality, "Предварительная диагностика по локальным эталонам");
        ToolTip.SetTip(_saveReference, "Локальная копия документа и проверенного текста для сравнения качества распознавания");
        SetId(_export, "TextExport", "Сохранить TXT");
        SetId(_exportAll, "TextExportAll", "Сохранить общий TXT");
        SetId(_previousPage, "TextPreviousPage", "Предыдущая страница");
        SetId(_nextPage, "TextNextPage", "Следующая страница");
        SetId(_pageStatus, "TextPageStatus", "Текущая страница");
        SetId(_reviewExpander, "TextReviewSections", "Строки на странице");
        SetId(_reviewBlocks, "TextReviewBlocks", "Распознанные строки страницы");
        SetId(_uncertainOnly, "TextUncertainOnly", "Только сомнительные строки");
        SetId(_nextUncertain, "TextNextUncertain", "Следующая сомнительная строка");
        SetId(_blockReviewed, "TextBlockReviewed", "Строка проверена");
        SetId(_uncertainCount, "TextUncertainCount", "Осталось сомнительных строк");
        SetId(_preview, "TextSourcePreview", "Предпросмотр документа");
        SetId(_highlightLayer, "TextSourceHighlight", "Выбранная область документа");
        ToolTip.SetTip(_previousPage, "Предыдущая страница"); ToolTip.SetTip(_nextPage, "Следующая страница");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var toolbar = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(18, 12) };
        toolbar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            Children =
            {
                BrandAssets.CreateMark(30),
                new TextBlock { Text = "ФормПоток: Текст", FontSize = 22, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        var privacy = new Button { Content = "Конфиденциальность" };
        SetId(privacy, "TextPrivacy", "Конфиденциальность");
        privacy.Click += async (_, _) => await ShowPrivacyAsync();
        toolbar.Children.Add(new WrapPanel { Children = { _add, _recognize, _cancel, _approve, _saveReference, _quality, _export, _exportAll, privacy } });
        root.Children.Add(toolbar);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,*"), Margin = new Avalonia.Thickness(12, 0) };
        Grid.SetRow(body, 1);
        body.Children.Add(_queue);
        var source = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Avalonia.Thickness(12, 0) };
        source.Children.Add(new TextBlock { Text = "Исходный документ", FontWeight = FontWeight.SemiBold });
        _previewLayer.Children.Add(_preview);
        _previewLayer.Children.Add(_highlightLayer);
        var sourceScroll = new ScrollViewer { Content = _previewLayer };
        Grid.SetRow(sourceScroll, 1); source.Children.Add(sourceScroll);
        var sourceToolbar = new WrapPanel { Margin = new Avalonia.Thickness(0, 6) };
        var open = new Button { Content = "Открыть исходник" };
        open.Click += (_, _) =>
        {
            if (Selected is not { } selected) return;
            try { Process.Start(new ProcessStartInfo(_workspace.ResolveSourcePath(selected)) { UseShellExecute = true }); }
            catch { SetStatus("Не удалось открыть исходник. Проверьте установленную программу просмотра."); }
        };
        sourceToolbar.Children.Add(_previousPage); sourceToolbar.Children.Add(_pageStatus); sourceToolbar.Children.Add(_nextPage); sourceToolbar.Children.Add(open);
        Grid.SetRow(sourceToolbar, 2); source.Children.Add(sourceToolbar);
        Grid.SetColumn(source, 1); body.Children.Add(source);
        var text = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Margin = new Avalonia.Thickness(0, 0, 0, 6) };
        text.Children.Add(new TextBlock { Text = "Текст", FontWeight = FontWeight.SemiBold });
        var findBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 4,
            Margin = new Avalonia.Thickness(0, 4, 0, 4) };
        findBar.Children.Add(_find);
        Grid.SetColumn(_findPrevious, 1); findBar.Children.Add(_findPrevious);
        Grid.SetColumn(_findNext, 2); findBar.Children.Add(_findNext);
        Grid.SetColumn(_findCount, 3); findBar.Children.Add(_findCount);
        Grid.SetRow(findBar, 1); text.Children.Add(findBar);
        _reviewExpander.Content = new StackPanel
        {
            Spacing = 4,
            Children = { new WrapPanel { Children = { _uncertainOnly, _nextUncertain, _uncertainCount } }, _reviewEmpty, _reviewBlocks, _blockReviewed },
        };
        Grid.SetRow(_reviewExpander, 2); text.Children.Add(_reviewExpander);
        Grid.SetRow(_editor, 3); text.Children.Add(_editor);
        Grid.SetColumn(text, 2); body.Children.Add(text);
        root.Children.Add(body);
        var footer = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(18, 10) };
        footer.Children.Add(_status);
        _importFailures.Content = _importFailureList;
        footer.Children.Add(_importFailures);
        _recognitionProgress.Children.Add(_progressLabel);
        _recognitionProgress.Children.Add(_queueProgress);
        _recognitionProgress.Children.Add(_ocrActivity);
        footer.Children.Add(_recognitionProgress);
        footer.Children.Add(new TextBlock { Text = "Документы и правки хранятся на этом компьютере. Передача данных выключена.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
        var version = typeof(LocalTextWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(LocalTextWindow).Assembly.GetName().Version?.ToString(3) ?? "неизвестна";
        var versionLabel = new TextBlock { Text = $"Версия {version} | {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}", FontSize = 12, Foreground = Brushes.DimGray };
        SetId(versionLabel, "TextProductVersion", "Версия программы");
        footer.Children.Add(versionLabel);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        return root;
    }

    internal async Task InitializeAsync()
    {
        try
        {
            await _workspace.LoadAsync();
            _healthy = true;
            RefreshQueue();
            SetStatus(Selected?.Status == LocalTextStatus.Failed ? LocalTextFailureMessages.Describe(Selected.Failure)
                : _workspace.Documents.Count == 0 ? "Добавьте документы." : "Очередь восстановлена.");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                _consent = await _consentStore.LoadAsync(timeout.Token);
                await _statistics.ReadPendingAsync(timeout.Token);
            }
            catch
            {
                _consent = new();
                SetStatus("Локальная работа доступна. Служебная статистика недоступна и отключена.");
            }
        }
        catch { _healthy = false; SetStatus("Не удалось прочитать локальную очередь. Данные сохранены без изменений. Закройте программу и сохраните копию рабочего каталога."); }
        RefreshCommands();
    }

    internal Task ImportPathsAsync(IEnumerable<string> paths) => RunOperationAsync(async cancellationToken =>
    {
        _importFailures.IsVisible = false;
        _importFailureList.ItemsSource = null;
        var knownIds = _workspace.Documents.Select(document => document.Id).ToHashSet();
        var added = 0;
        var duplicates = 0;
        var failures = new List<string>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await _workspace.ImportAsync(path, cancellationToken);
                if (knownIds.Add(document.Id)) added++;
                else duplicates++;
                _selectedId = document.Id;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (IsImportFileError(error))
            {
                if (_workspace.Documents.Any(document => !knownIds.Contains(document.Id))) throw;
                failures.Add($"{Path.GetFileName(path)}: {DescribeImportFileError(error)}");
            }
        }
        _importFailureList.ItemsSource = failures;
        _importFailures.Header = $"Не удалось добавить: {failures.Count}";
        _importFailures.IsExpanded = true;
        _importFailures.IsVisible = failures.Count > 0;
        RefreshQueue();
        var summary = $"Импорт завершён: добавлено {added}, дубликатов {duplicates}, не удалось {failures.Count}.";
        SetStatus(failures.Count == 1 ? $"{summary} {failures[0]}" : summary);
    });

    private static bool IsImportFileError(Exception error) => error is LocalTextSourceLimitException
        or InvalidDataException or FileNotFoundException or DirectoryNotFoundException
        or UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException;

    private static string DescribeImportFileError(Exception error) => error switch
    {
        LocalTextSourceLimitException => "Файл больше 20 МиБ. Выберите меньший файл.",
        InvalidDataException when error.Message == "Unsupported source format." => "Неподдерживаемый формат. Выберите PDF, PNG или JPEG.",
        InvalidDataException when error.Message == "Source is empty." => "Файл пустой.",
        InvalidDataException when error.Message == "Workspace document limit reached." => "Достигнут лимит очереди: 500 документов.",
        FileNotFoundException or DirectoryNotFoundException => "Файл не найден. Проверьте, что он доступен.",
        UnauthorizedAccessException => "Нет доступа к файлу. Проверьте разрешения Windows.",
        _ => "Не удалось прочитать файл. Проверьте формат и доступ к нему.",
    };

    internal Task RecognizeQueueAsync() => RunOperationAsync(async cancellationToken =>
    {
        await _workspace.SaveAsync(cancellationToken);
        var pending = _workspace.Documents.Where(document => document.Status is LocalTextStatus.Queued or LocalTextStatus.Failed).ToArray();
        if (pending.Length == 0) return;
        StartRecognitionProgress(pending.Length);
        var completed = 0;
        for (var index = 0; index < pending.Length; index++)
        {
            var document = pending[index];
            cancellationToken.ThrowIfCancellationRequested();
            _workspace.Replace(document with { Status = LocalTextStatus.Recognizing });
            var timer = Stopwatch.StartNew();
            var pageCount = 0;
            var blockCount = 0;
            var outcome = LocalRecognitionOutcome.Completed;
            try
            {
                await _workspace.SaveAsync(cancellationToken);
                RefreshQueue(); SetStatus($"Распознавание {index + 1}/{pending.Length}: {document.DisplayName}");
                var extraction = await _runtime.ExtractAsync(_workspace.ResolveSourcePath(document), cancellationToken);
                pageCount = extraction.Pages.Count;
                blockCount = extraction.Pages.Sum(page => page.TextBlocks.Count);
                var recognized = document.WithExtraction(extraction);
                _workspace.Replace(recognized);
                if (recognized.Status == LocalTextStatus.Failed) outcome = LocalRecognitionOutcome.Failed;
            }
            catch (OperationCanceledException) { outcome = LocalRecognitionOutcome.Cancelled; _workspace.Replace(document); throw; }
            catch (Exception error)
            {
                outcome = LocalRecognitionOutcome.Failed;
                _workspace.Replace(document with { Status = LocalTextStatus.Failed, Failure = LocalTextFailureMessages.Classify(error) });
            }
            finally
            {
                await _workspace.SaveAsync(); RefreshQueue();
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _statistics.RecordRecognitionAsync(_consent, Math.Min(pageCount, LocalUsageStatisticsOutbox.MaximumPageCount),
                        Math.Min(blockCount, LocalUsageStatisticsOutbox.MaximumBlockCount),
                        timer.Elapsed > LocalUsageStatisticsOutbox.MaximumDuration ? LocalUsageStatisticsOutbox.MaximumDuration : timer.Elapsed, outcome, timeout.Token);
                }
                catch { /* Optional statistics must not interrupt recognition or saving. */ }
                if (outcome != LocalRecognitionOutcome.Cancelled) UpdateRecognitionProgress(++completed, pending.Length);
            }
        }
        var failures = _workspace.Documents.Count(document => document.Status == LocalTextStatus.Failed);
        SetStatus(failures == 0 ? "Распознавание завершено. Проверьте текст и подтвердите результат."
            : $"Не удалось обработать файлов: {failures}. " + LocalTextFailureMessages.Describe(
                _workspace.Documents.First(document => document.Status == LocalTextStatus.Failed).Failure));
    });

    internal Task ApproveSelectedAsync() => RunOperationAsync(async cancellationToken =>
    {
        if (Selected is not { } selected) return;
        var updated = selected.WithEdit(_editor.Text ?? "");
        var remaining = updated.RemainingUncertainCount();
        if (remaining > 0 && _pendingApprovalId != selected.Id)
        {
            _pendingApprovalId = selected.Id;
            _approve.Content = "Подтвердить с остатком";
            SetStatus($"Осталось непроверенных сомнительных строк: {remaining}. Нажмите «Подтвердить с остатком» ещё раз, чтобы продолжить.");
            return;
        }
        _pendingApprovalId = null;
        _approve.Content = "Подтвердить текст";
        _workspace.Replace(updated.Approve());
        await _workspace.SaveAsync(cancellationToken);
        RefreshQueue(); SetStatus(remaining == 0 ? "Текст подтверждён. Можно сохранить TXT."
            : $"Текст подтверждён. Непроверенных сомнительных строк: {remaining}. Можно сохранить TXT.");
    });

    internal Task SaveReferenceSelectedAsync() => RunOperationAsync(async cancellationToken =>
    {
        if (Selected is not { Status: LocalTextStatus.Reviewed } selected) return;
        try
        {
            var sample = await _qualityLab.SaveReviewedAsync(selected, _workspace.ResolveSourcePath(selected), cancellationToken);
            SetStatus($"Эталон сохранён локально, версия {sample.Version}. Документ не отправлялся в сеть.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus("Не удалось сохранить эталон. Проверьте доступ к рабочей папке и свободное место.");
        }
    });

    internal async Task<Window> CreateQualitySummaryAsync()
    {
        var dialog = new Window
        {
            Title = "Предварительная диагностика качества", Icon = BrandAssets.CreateWindowIcon(), Width = 500, MinWidth = 420,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
        };
        var references = new TextBlock { FontSize = 14 };
        var holdout = new TextBlock { FontSize = 14 };
        var availability = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brushes.DimGray };
        var metrics = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Firebrick, IsVisible = false };
        var export = new Button { Content = "Сохранить диагностику JSON", IsEnabled = false };
        var close = new Button { Content = "Закрыть" };
        SetId(references, "TextQualityReferences", "Число локальных эталонов");
        SetId(holdout, "TextQualityHoldout", "Число записей в контрольном разделе");
        SetId(availability, "TextQualityAvailability", "Статус предварительной диагностики");
        SetId(metrics, "TextQualityMetrics", "Диагностические ошибки распознавания");
        SetId(error, "TextQualityError", "Ошибка локального отчёта");
        SetId(export, "TextQualityExport", "Сохранить предварительный диагностический отчёт JSON");
        SetId(close, "TextQualityClose", "Закрыть сводку качества");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        close.Click += (_, _) => dialog.Close();
        LocalQualityReport? report = null;
        try
        {
            var samples = await _qualityLab.ListSamplesAsync();
            var holdoutCount = samples.Count(sample => sample.Split == "holdout");
            references.Text = $"Локальных эталонов: {samples.Count}";
            holdout.Text = $"Записей в контрольном разделе: {holdoutCount}";
            if (holdoutCount < 30)
            {
                availability.Text = "Для расчёта предварительных показателей нужно не менее 30 записей. Независимость источников не подтверждена.";
                metrics.Text = "CER и WER: не рассчитаны.";
            }
            else
            {
                report = await LocalQualityEvaluator.EvaluateAsync(_qualityLab, []);
                var baseline = report.Candidates.Single(candidate => candidate.Candidate == "current-at-review");
                if (baseline.Complete && baseline.Cer.HasValue && baseline.Wer.HasValue)
                {
                    availability.Text = "Предварительная диагностика. Независимость источников не подтверждена; эти показатели не подходят для решения о выпуске.";
                    metrics.Text = $"На момент проверки: CER {baseline.Cer.Value:P1} · WER {baseline.Wer.Value:P1}";
                    export.IsEnabled = true;
                }
                else
                {
                    availability.Text = "Диагностика неполная. Проверьте локальные эталоны.";
                    metrics.Text = "CER и WER: не рассчитаны.";
                    report = null;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException)
        {
            references.Text = "Локальные эталоны: не удалось прочитать.";
            holdout.Text = "Записей в контрольном разделе: неизвестно.";
            availability.Text = "Диагностика недоступна. Проверьте локальное хранилище.";
            metrics.Text = "CER и WER: не рассчитаны.";
        }
        export.Click += async (_, _) =>
        {
            if (report is null) return;
            var file = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить диагностический отчёт", SuggestedFileName = "Предварительная диагностика.json",
                DefaultExtension = "json", FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            export.IsEnabled = false;
            error.IsVisible = false;
            try
            {
                var fullPath = Path.GetFullPath(path);
                var workspaceRoot = _workspace.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Report destination overlaps local workspace.");
                var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, SerializeDiagnosticReport(report));
                    File.Move(temporary, fullPath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                error.Text = "Не удалось сохранить отчёт. Проверьте доступ к папке и свободное место.";
                error.IsVisible = true;
            }
            finally { export.IsEnabled = true; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { close, export } };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24, 18), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Предварительная диагностика", FontSize = 22, FontWeight = FontWeight.SemiBold },
                references, holdout, availability, metrics,
                new TextBlock { Text = "Отчёт содержит только диагностические показатели и контрольный хэш. Документы и текст никуда не отправляются.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DimGray },
                error, buttons,
            },
        };
        return dialog;
    }

    internal static byte[] SerializeDiagnosticReport(LocalQualityReport report)
    {
        var baseline = report.Candidates.Single(candidate => candidate.Candidate == "current-at-review");
        if (report.SourceDocuments < 30 || !baseline.Complete || !baseline.Cer.HasValue || !baseline.Wer.HasValue)
            throw new InvalidOperationException("Incomplete diagnostic report cannot be exported.");
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            reportType = "preliminary-diagnostic",
            sourceGroupIndependenceVerified = false,
            holdoutRecords = report.SourceDocuments,
            datasetSha256 = report.DatasetSha256,
            normalizationPolicy = report.NormalizationPolicy,
            baselineCer = baseline.Cer,
            baselineWer = baseline.Wer,
        });
    }

    internal async Task SetStatisticsConsentAsync(bool enabled)
    {
        var consent = new LocalLearningConsent(Statistics: enabled, Examples: false);
        if (!enabled) _consent = new();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await _consentStore.SaveAsync(consent, timeout.Token);
        _consent = consent;
    }

    private Task ShowPrivacyAsync() =>
        new PrivacyDialog(_consent.StatisticsEnabled, SetStatisticsConsentAsync).ShowDialog(this);

    private async Task PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Добавить документы", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("PDF и изображения") { Patterns = ["*.pdf", "*.png", "*.jpg", "*.jpeg"] }],
        });
        await ImportPathsAsync(files.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Cast<string>());
    }

    private async Task PickExportAsync(bool combine)
    {
        var selected = Selected;
        CombinedExportSelection? selection = null;
        if (combine)
        {
            selection = await new CombinedExportDialog(_workspace.Documents).ShowDialog<CombinedExportSelection?>(this);
            if (selection is null) return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить текст", SuggestedFileName = combine ? "Документы.txt" : $"{Path.GetFileNameWithoutExtension(selected?.DisplayName)}.txt",
            DefaultExtension = "txt", FileTypeChoices = [new FilePickerFileType("Текст UTF-8") { Patterns = ["*.txt"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        if (File.Exists(path) && !await ConfirmOverwriteAsync(path)) return;
        await RunOperationAsync(async token =>
        {
            if (selection is not null) await _workspace.ExportCombinedAsync(path, selection.DocumentIds, selection.IncludeHeaders, token);
            else if (selected is not null) await _workspace.ExportDocumentAsync(selected.Id, path, token);
            SetStatus("Текст сохранён.");
        });
    }

    private async Task<bool> ConfirmOverwriteAsync(string path)
    {
        var dialog = new Window
        {
            Title = "Заменить файл?", Icon = BrandAssets.CreateWindowIcon(), Width = 430, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White,
        };
        var replace = new Button { Content = "Заменить", MinWidth = 90 };
        var cancel = new Button { Content = "Отмена", MinWidth = 80 };
        replace.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = $"Файл «{Path.GetFileName(path)}» уже существует. Заменить его?", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, replace } },
            },
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy || !_healthy || _closing) return Task.CompletedTask;
        _activeOperation = ExecuteAsync(operation);
        return _activeOperation;
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> operation)
    {
        _busy = true;
        _cancellation?.Dispose(); _cancellation = new CancellationTokenSource();
        _saveTimer.Stop(); RefreshCommands();
        try { await _workspace.SaveAsync(_cancellation.Token); await operation(_cancellation.Token); }
        catch (OperationCanceledException) { SetStatus("Операция отменена. Сохранённые результаты доступны в очереди."); }
        catch (LocalTextSourceLimitException) { SetStatus(LocalTextFailureMessages.Describe(LocalTextFailure.SourceLimit)); }
        catch { SetStatus("Не удалось завершить операцию. Проверьте формат файла, доступ к папке и свободное место."); }
        finally
        {
            _busy = false; _recognitionProgress.IsVisible = false; RefreshQueue(); RefreshCommands();
            if (_healthy && !_closing) _saveTimer.Start();
        }
    }

    private async Task<bool> SaveEditsAsync()
    {
        if (!_healthy) return true;
        try { await _workspace.SaveAsync(); return true; }
        catch { SetStatus("Не удалось сохранить правки. Проверьте свободное место и доступ к папке; не закрывайте программу."); return false; }
    }

    private void RefreshQueue()
    {
        _updatingEditor = true;
        _queue.ItemsSource = _workspace.Documents.ToArray();
        _queue.SelectedItem = Selected ?? _workspace.Documents.FirstOrDefault();
        _updatingEditor = false;
        SelectDocument(showFailureDetails: false);
    }

    private void SelectDocument(bool showFailureDetails = true)
    {
        if (_updatingEditor) return;
        var selectedId = (_queue.SelectedItem as LocalTextDocument)?.Id;
        var selected = _workspace.Documents.FirstOrDefault(document => document.Id == selectedId);
        if (_selectedId != selected?.Id)
        {
            _pageIndex = 0;
            _pendingApprovalId = null;
            _approve.Content = "Подтвердить текст";
        }
        _selectedId = selected?.Id;
        _updatingEditor = true; _editor.Text = selected?.EditedText ?? ""; _updatingEditor = false;
        RefreshTextSearch();
        RefreshPreview(reportErrors: showFailureDetails); RefreshCommands();
        if (showFailureDetails && !_busy && selected?.Status == LocalTextStatus.Failed) SetStatus(LocalTextFailureMessages.Describe(selected.Failure));
    }

    private void RefreshPreview(bool reportErrors = true)
    {
        _preview.Source = null; _bitmap?.Dispose(); _bitmap = null; _previewSourceSize = default;
        _highlightedBlock = null;
        _highlightLayer.Children.Clear();
        var selected = Selected;
        var pages = selected?.Extraction?.Pages.Count ?? 1;
        _pageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, pages - 1));
        RefreshReviewBlocks();
        _pageStatus.Text = selected is null ? "" : $"{_pageIndex + 1}/{pages}";
        _previousPage.IsEnabled = selected is not null && _pageIndex > 0;
        _nextPage.IsEnabled = selected is not null && _pageIndex + 1 < pages;
        if (selected is null) return;
        var path = _workspace.ResolveSourcePath(selected);
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.Combine(_workspace.Root, "ocr-runs") + Path.DirectorySeparatorChar;
            path = selected.Extraction?.Artifacts.FirstOrDefault(artifact => IsPreviewArtifact(artifact, root)) ?? "";
        }
        try
        {
            if (File.Exists(path))
            {
                (_bitmap, _previewSourceSize) = LocalTextPreview.Decode(path);
                _preview.Source = _bitmap;
            }
        }
        catch { if (reportErrors) SetStatus("Предпросмотр недоступен. Исходник можно открыть в программе просмотра."); }
    }

    private void RefreshReviewBlocks(int? selectedBlockIndex = null)
    {
        var selected = Selected;
        var page = selected?.Extraction?.Pages.ElementAtOrDefault(_pageIndex);
        _updatingReviewBlocks = true;
        _visibleBlocks = page?.TextBlocks.Select((block, index) => (Index: index, Block: block))
            .Where(item => _uncertainOnly.IsChecked != true || item.Block.Confidence < 0.75)
            .ToArray() ?? [];
        _reviewBlocks.ItemsSource = _visibleBlocks.Select(item => ReviewBlockLabel(item.Block,
            selected?.IsBlockReviewed(_pageIndex, item.Index) == true)).ToArray();
        _reviewBlocks.SelectedIndex = selectedBlockIndex is null ? -1 : Array.FindIndex(_visibleBlocks, item => item.Index == selectedBlockIndex);
        _updatingReviewBlocks = false;
        _updatingReviewMark = true;
        _blockReviewed.IsChecked = selectedBlockIndex is not null && selected?.IsBlockReviewed(_pageIndex, selectedBlockIndex.Value) == true;
        _updatingReviewMark = false;
        _blockReviewed.IsVisible = _reviewBlocks.SelectedIndex >= 0;
        _uncertainCount.Text = selected?.Extraction is null ? "" : $"Осталось: {selected.RemainingUncertainCount()}";
        _reviewExpander.IsVisible = selected?.Extraction?.Pages.Any(item => item.TextBlocks.Count > 0) == true;
        _reviewExpander.Header = page is null ? "Строки на странице" : $"Строки на странице ({page.TextBlocks.Count})";
        _reviewBlocks.IsVisible = _visibleBlocks.Length > 0;
        _reviewEmpty.IsVisible = _reviewExpander.IsVisible && _visibleBlocks.Length == 0;
        _reviewEmpty.Text = page is { TextBlocks.Count: 0 } ? "На этой странице нет распознанных строк."
            : "На этой странице нет строк с низкой уверенностью.";
        _highlightedBlock = null;
        _highlightLayer.Children.Clear();
        if (_reviewBlocks.SelectedIndex >= 0) SelectReviewBlock();
    }

    private void SelectReviewBlock()
    {
        if (_updatingReviewBlocks || _reviewBlocks.SelectedIndex < 0 || _reviewBlocks.SelectedIndex >= _visibleBlocks.Length
            || Selected is not { Extraction: not null } selected)
            return;
        var item = _visibleBlocks[_reviewBlocks.SelectedIndex];
        _updatingReviewMark = true;
        _blockReviewed.IsChecked = selected.IsBlockReviewed(_pageIndex, item.Index);
        _updatingReviewMark = false;
        _blockReviewed.IsVisible = true;
        _highlightedBlock = item.Block;
        var mapped = ReviewLinking.TryGetTextSpan(selected, _pageIndex, item.Index, out var start, out var length);
        if (mapped)
        {
            _editor.Focus();
            _editor.SelectionStart = start;
            _editor.SelectionEnd = start + length;
        }
        var highlighted = RefreshHighlight();
        SetStatus(highlighted
            ? mapped ? "Строка выделена в тексте и на странице." : "Область найдена на странице; положение в исправленном тексте не определено."
            : mapped ? "Строка выделена в тексте. Точное положение на странице недоступно."
                : "Точное положение строки после правок не определено.");
    }

    private bool RefreshHighlight()
    {
        _highlightLayer.Children.Clear();
        if (_highlightedBlock is null || _bitmap is null || Selected?.Extraction?.Pages.ElementAtOrDefault(_pageIndex) is not { } page)
            return false;
        var rect = ReviewLinking.HighlightRect(page, _highlightedBlock,
            new Avalonia.Size(_previewSourceSize.Width, _previewSourceSize.Height), _highlightLayer.Bounds.Size);
        if (rect is null) return false;
        var marker = new Border
        {
            Width = rect.Value.Width,
            Height = rect.Value.Height,
            Background = new SolidColorBrush(Color.FromArgb(42, 255, 160, 35)),
            BorderBrush = Brushes.DarkOrange,
            BorderThickness = new Avalonia.Thickness(2),
            CornerRadius = new Avalonia.CornerRadius(2),
        };
        Canvas.SetLeft(marker, rect.Value.X);
        Canvas.SetTop(marker, rect.Value.Y);
        _highlightLayer.Children.Add(marker);
        return true;
    }

    private static string ReviewBlockLabel(ExtractionTextBlock block, bool reviewed)
    {
        var text = block.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > 100) text = text[..97] + "...";
        return reviewed ? $"✓ {text}" : block.Confidence < 0.75 ? $"Проверить {block.Confidence:P0}: {text}" : text;
    }

    private void NavigateUncertain()
    {
        if (Selected is not { Extraction: not null } selected) return;
        var startPage = _pageIndex;
        var startBlock = _reviewBlocks.SelectedIndex >= 0 ? _visibleBlocks[_reviewBlocks.SelectedIndex].Index : -1;
        for (var offset = 0; offset < selected.Extraction.Pages.Count; offset++)
        {
            var pageIndex = (startPage + offset) % selected.Extraction.Pages.Count;
            var blocks = selected.Extraction.Pages[pageIndex].TextBlocks;
            for (var blockIndex = offset == 0 ? startBlock + 1 : 0; blockIndex < blocks.Count; blockIndex++)
            {
                if (blocks[blockIndex].Confidence >= 0.75 || selected.IsBlockReviewed(pageIndex, blockIndex)) continue;
                _pageIndex = pageIndex;
                _uncertainOnly.IsChecked = true;
                RefreshPreview();
                _reviewExpander.IsExpanded = true;
                _reviewBlocks.SelectedIndex = Array.FindIndex(_visibleBlocks, item => item.Index == blockIndex);
                return;
            }
        }
        if (startBlock < 0) return;
        for (var blockIndex = 0; blockIndex <= startBlock; blockIndex++)
        {
            var block = selected.Extraction.Pages[startPage].TextBlocks[blockIndex];
            if (block.Confidence >= 0.75 || selected.IsBlockReviewed(startPage, blockIndex)) continue;
            _uncertainOnly.IsChecked = true;
            _reviewBlocks.SelectedIndex = Array.FindIndex(_visibleBlocks, item => item.Index == blockIndex);
            return;
        }
    }

    private void RefreshCommands()
    {
        _add.IsEnabled = _healthy && !_busy && !_closing;
        _recognize.IsEnabled = _healthy && !_busy && !_closing && _workspace.Documents.Any(document => document.Status is LocalTextStatus.Queued or LocalTextStatus.Failed);
        _cancel.IsEnabled = _busy;
        _queue.IsEnabled = _healthy && !_busy && !_closing;
        _reviewBlocks.IsEnabled = _healthy && !_busy && !_closing;
        _uncertainOnly.IsEnabled = _healthy && !_busy && !_closing;
        _nextUncertain.IsEnabled = _healthy && !_busy && !_closing && Selected?.RemainingUncertainCount() > 0;
        _blockReviewed.IsEnabled = _healthy && !_busy && !_closing && _reviewBlocks.SelectedIndex >= 0;
        _editor.IsEnabled = _healthy && !_busy && !_closing && Selected?.Extraction is not null;
        _find.IsEnabled = _editor.IsEnabled;
        _findPrevious.IsEnabled = _findNext.IsEnabled = _find.IsEnabled && _searchMatches.Length > 0;
        _approve.IsEnabled = _healthy && !_busy && !_closing && Selected?.Extraction is not null && !string.IsNullOrWhiteSpace(Selected.EditedText);
        _saveReference.IsEnabled = _healthy && !_busy && !_closing && Selected?.Status == LocalTextStatus.Reviewed;
        _quality.IsEnabled = _healthy && !_busy && !_closing;
        _export.IsEnabled = _healthy && !_busy && !_closing && Selected?.Status == LocalTextStatus.Reviewed;
        _exportAll.IsEnabled = _healthy && !_busy && !_closing && _workspace.Documents.Any(document => document.Status == LocalTextStatus.Reviewed);
    }

    private void RefreshTextSearch()
    {
        var query = _find.Text?.Trim() ?? "";
        var text = _editor.Text ?? "";
        var matches = new List<(int Start, int Length)>();
        if (query.Length > 0 && Selected?.Extraction is not null)
        {
            for (var start = 0; start < text.Length;)
            {
                var found = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                matches.Add((found, query.Length));
                start = found + query.Length;
            }
        }
        _searchMatches = matches.ToArray();
        _searchIndex = -1;
        _findCount.Text = _searchMatches.Length == 0 ? "0" : $"Найдено: {_searchMatches.Length}";
        _findPrevious.IsEnabled = _findNext.IsEnabled = _find.IsEnabled && _searchMatches.Length > 0;
    }

    private void NavigateSearch(int direction, bool focusEditor = true)
    {
        if (_searchMatches.Length == 0 || !_find.IsEnabled) return;
        if (_searchIndex < 0)
        {
            var caret = _editor.SelectionEnd;
            _searchIndex = direction > 0
                ? Array.FindIndex(_searchMatches, match => match.Start >= caret)
                : Array.FindLastIndex(_searchMatches, match => match.Start < caret);
            if (_searchIndex < 0) _searchIndex = direction > 0 ? 0 : _searchMatches.Length - 1;
        }
        else
        {
            _searchIndex = (_searchIndex + direction + _searchMatches.Length) % _searchMatches.Length;
        }
        var match = _searchMatches[_searchIndex];
        if (focusEditor) _editor.Focus();
        _editor.SelectionStart = match.Start;
        _editor.SelectionEnd = match.Start + match.Length;
        _findCount.Text = $"{_searchIndex + 1} из {_searchMatches.Length}";
    }

    private void SetStatus(string text) { _status.Text = text; AutomationProperties.SetName(_status, text); }

    private void StartRecognitionProgress(int total)
    {
        _queueProgress.Maximum = total;
        UpdateRecognitionProgress(0, total);
        _recognitionProgress.IsVisible = true;
    }

    private void UpdateRecognitionProgress(int completed, int total)
    {
        _queueProgress.Value = completed;
        _progressLabel.Text = $"Обработано {completed} из {total}";
        AutomationProperties.SetName(_queueProgress, _progressLabel.Text);
    }

    private bool IsPreviewArtifact(string artifact, string root)
    {
        try
        {
            return Path.GetFullPath(artifact).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(artifact) == $"page-{_pageIndex + 1:0000}.png";
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static void SetId(Control control, string id, string name) { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, name); }
    private static string StatusLabel(LocalTextStatus status) => status switch
    {
        LocalTextStatus.Queued => "В очереди", LocalTextStatus.Recognizing => "Распознавание",
        LocalTextStatus.NeedsReview => "Нужна проверка", LocalTextStatus.Reviewed => "Подтверждено", _ => "Ошибка",
    };
}

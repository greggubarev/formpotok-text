using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FormPotok.Application.Contracts;
using FormPotok.Infrastructure.OcrRuntime;
using FormPotok.Ocr.Contracts;
using FormPotok.PilotCore.LocalText;
using FormPotok.PilotCore.Learning;
using SkiaSharp;
using Xunit;

namespace FormPotok.App.UiSmoke.Tests;

public sealed class LocalTextShellSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "formpotok-text-ui-tests", Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public void CombinedExportDialog_OnlyReviewedAndRequiresSelection()
    {
        var first = new LocalTextDocument(Guid.NewGuid(), "one.png", "", "", LocalTextStatus.Reviewed, EditedText: "one");
        var pending = new LocalTextDocument(Guid.NewGuid(), "pending.png", "", "", LocalTextStatus.NeedsReview);
        var third = new LocalTextDocument(Guid.NewGuid(), "three.png", "", "", LocalTextStatus.Reviewed, EditedText: "three");
        var dialog = new CombinedExportDialog([first, pending, third]);
        dialog.Show();
        var list = Find<ListBox>(dialog, "CombinedExportDocuments");
        Assert.Equal(new[] { first.Id, third.Id }, list.ItemsSource!.Cast<LocalTextDocument>().Select(document => document.Id));
        list.SelectedItem = third;
        Find<Button>(dialog, "CombinedExportUp").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { third.Id, first.Id }, list.ItemsSource!.Cast<LocalTextDocument>().Select(document => document.Id));
        Assert.True(Find<Button>(dialog, "CombinedExportContinue").IsEnabled);
        Assert.True(Find<CheckBox>(dialog, "CombinedExportHeaders").IsChecked);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task QualitySummary_WithoutReferences_DoesNotShowOrExportMetrics()
    {
        var window = CreateWindow(new FakeRuntime());
        window.Show(); await window.InitializeAsync();
        Assert.True(Find<Button>(window, "TextQuality").IsEnabled);
        var dialog = await window.CreateQualitySummaryAsync();
        dialog.Show(window);
        Assert.Contains("0", Find<TextBlock>(dialog, "TextQualityReferences").Text);
        Assert.Contains("разделе: 0", Find<TextBlock>(dialog, "TextQualityHoldout").Text);
        Assert.Contains("Независимость источников не подтверждена", Find<TextBlock>(dialog, "TextQualityAvailability").Text);
        Assert.Contains("не рассчитаны", Find<TextBlock>(dialog, "TextQualityMetrics").Text);
        Assert.False(Find<Button>(dialog, "TextQualityExport").IsEnabled);
        dialog.Close(); await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task QualitySummary_WithOneSavedReference_ShowsInsufficientHoldout()
    {
        var window = CreateWindow(new FakeRuntime());
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await HoldoutSourceAsync(0)]);
        await window.RecognizeQueueAsync();
        await window.ApproveSelectedAsync();
        await window.SaveReferenceSelectedAsync();
        var dialog = await window.CreateQualitySummaryAsync();
        dialog.Show(window);
        Assert.Contains("1", Find<TextBlock>(dialog, "TextQualityReferences").Text);
        Assert.Contains("разделе: 1", Find<TextBlock>(dialog, "TextQualityHoldout").Text);
        Assert.Contains("не рассчитаны", Find<TextBlock>(dialog, "TextQualityMetrics").Text);
        Assert.False(Find<Button>(dialog, "TextQualityExport").IsEnabled);
        dialog.Close(); await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task QualitySummary_CompleteHoldoutShowsMetrics_ButDamagedDataNeverShowsStaleQuality()
    {
        var lab = new LocalTextQualityLab(Path.Combine(_root, "workspace", "quality-lab"));
        using (var sourceWorkspace = new LocalTextWorkspace(Path.Combine(_root, "sample-workspace")))
        {
            for (var index = 0; index < 30; index++)
            {
                var source = await HoldoutSourceAsync(index);
                var imported = await sourceWorkspace.ImportAsync(source);
                var extraction = new ExtractionEnvelope("local", "test", "1", [
                    new ExtractionPage(1, 100, 100, [new ExtractionTextBlock("1", "Кит мир", 0.9, [])], []),
                ], [], [], []);
                await lab.SaveReviewedAsync(imported.WithExtraction(extraction).WithEdit("Кот мир").Approve(),
                    sourceWorkspace.ResolveSourcePath(imported));
            }
        }
        var window = CreateWindow(new FakeRuntime());
        window.Show(); await window.InitializeAsync();
        var dialog = await window.CreateQualitySummaryAsync();
        dialog.Show(window);
        Assert.Contains("30", Find<TextBlock>(dialog, "TextQualityReferences").Text);
        Assert.Contains("разделе: 30", Find<TextBlock>(dialog, "TextQualityHoldout").Text);
        Assert.Contains("Предварительная диагностика", Find<TextBlock>(dialog, "TextQualityAvailability").Text);
        Assert.Contains("Независимость источников не подтверждена", Find<TextBlock>(dialog, "TextQualityAvailability").Text);
        Assert.Contains("не подходят для решения о выпуске", Find<TextBlock>(dialog, "TextQualityAvailability").Text);
        Assert.Contains("CER", Find<TextBlock>(dialog, "TextQualityMetrics").Text);
        Assert.Contains("WER", Find<TextBlock>(dialog, "TextQualityMetrics").Text);
        Assert.True(Find<Button>(dialog, "TextQualityExport").IsEnabled);
        using (var exported = JsonDocument.Parse(LocalTextWindow.SerializeDiagnosticReport(
            await LocalQualityEvaluator.EvaluateAsync(lab, []))))
        {
            Assert.Equal("preliminary-diagnostic", exported.RootElement.GetProperty("reportType").GetString());
            Assert.False(exported.RootElement.GetProperty("sourceGroupIndependenceVerified").GetBoolean());
            Assert.Equal(30, exported.RootElement.GetProperty("holdoutRecords").GetInt32());
            Assert.False(exported.RootElement.TryGetProperty("SufficientHoldout", out _));
            Assert.DoesNotContain("Кот мир", exported.RootElement.ToString());
        }
        dialog.Close();

        var sample = (await lab.ListSamplesAsync()).First();
        File.Delete(Path.Combine(lab.Root, "baselines", sample.SourceSha256, sample.BaselineSha256 + ".txt"));
        var damaged = await window.CreateQualitySummaryAsync();
        damaged.Show(window);
        Assert.Contains("недоступна", Find<TextBlock>(damaged, "TextQualityAvailability").Text);
        Assert.Contains("не рассчитаны", Find<TextBlock>(damaged, "TextQualityMetrics").Text);
        Assert.False(Find<Button>(damaged, "TextQualityExport").IsEnabled);
        damaged.Close();

        await File.WriteAllTextAsync(Path.Combine(lab.Root, "samples", sample.SourceSha256 + ".json"), "{invalid-json}");
        var invalidMetadata = await window.CreateQualitySummaryAsync();
        invalidMetadata.Show(window);
        Assert.Contains("не удалось прочитать", Find<TextBlock>(invalidMetadata, "TextQualityReferences").Text);
        Assert.False(Find<Button>(invalidMetadata, "TextQualityExport").IsEnabled);
        invalidMetadata.Close(); await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task LocalStartup_DoesNotNeedLoginAndGatesExportUntilReview()
    {
        var window = CreateWindow(new FakeRuntime());
        window.Show();
        await window.InitializeAsync();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), control => control.Name == "LoginBox");
        Assert.True(Find<Button>(window, "TextAddFiles").IsEnabled);
        Assert.NotNull(window.Icon);
        Assert.Contains(window.GetVisualDescendants().OfType<Image>(), image => image.Source is not null && image.Width == 30);
        Assert.Contains("Версия", Find<TextBlock>(window, "TextProductVersion").Text);
        Assert.False(Find<Button>(window, "TextExport").IsEnabled);
        await window.ImportPathsAsync([await Source("source.png")]);
        await window.RecognizeQueueAsync();
        Assert.Equal("Распознанный текст", Find<TextBox>(window, "TextEditor").Text);
        Assert.False(Find<Button>(window, "TextExport").IsEnabled);
        await window.ApproveSelectedAsync();
        Assert.True(Find<Button>(window, "TextExport").IsEnabled);
        Find<TextBox>(window, "TextEditor").Text = "Исправленный текст";
        Assert.False(Find<Button>(window, "TextExport").IsEnabled);
        await window.FlushAndCloseAsync();
        Assert.False(File.Exists(Path.Combine(_root, "workspace", "auth-session.dat")));
    }

    [AvaloniaFact]
    public async Task SwitchingAndRestart_PreserveUnsavedEditorAndOriginalExtraction()
    {
        var window = CreateWindow(new FakeRuntime());
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("one.png", "one"), await Source("two.png", "two")]);
        await window.RecognizeQueueAsync();
        var queue = Find<ListBox>(window, "TextDocumentQueue");
        queue.SelectedIndex = 0;
        Find<TextBox>(window, "TextEditor").Text = "Первый исправлен";
        queue.SelectedIndex = 1;
        Find<TextBox>(window, "TextEditor").Text = "Второй исправлен";
        queue.SelectedIndex = 0;
        Assert.Equal("Первый исправлен", Find<TextBox>(window, "TextEditor").Text);
        await window.ApproveSelectedAsync();
        queue = Find<ListBox>(window, "TextDocumentQueue");
        queue.SelectedIndex = 1;
        Assert.Equal("Второй исправлен", Find<TextBox>(window, "TextEditor").Text);
        await window.FlushAndCloseAsync();
        var reopened = CreateWindow(new FakeRuntime());
        reopened.Show(); await reopened.InitializeAsync();
        var restoredQueue = Find<ListBox>(reopened, "TextDocumentQueue");
        restoredQueue.SelectedIndex = 0;
        Assert.Equal("Первый исправлен", Find<TextBox>(reopened, "TextEditor").Text);
        restoredQueue.SelectedIndex = 1;
        Assert.Equal("Второй исправлен", Find<TextBox>(reopened, "TextEditor").Text);
        await reopened.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task FindText_NavigatesCyrillicMatchesAndTracksEditsAndDocumentSwitches()
    {
        var window = CreateWindow(new FakeRuntime { Text = "Счёт 10\nСчёт 20\nИтого" });
        window.Show(); await window.InitializeAsync();
        var find = Find<TextBox>(window, "TextFind");
        var next = Find<Button>(window, "TextFindNext");
        var previous = Find<Button>(window, "TextFindPrevious");
        var count = Find<TextBlock>(window, "TextFindCount");
        Assert.False(find.IsEnabled);
        await window.ImportPathsAsync([await Source("one.png", "one"), await Source("two.png", "two")]);
        await window.RecognizeQueueAsync();
        var queue = Find<ListBox>(window, "TextDocumentQueue");
        queue.SelectedIndex = 0;
        var editor = Find<TextBox>(window, "TextEditor");
        find.Text = "счЁт";
        Assert.Equal("Найдено: 2", count.Text);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Счёт", editor.SelectedText);
        Assert.Equal(0, editor.SelectionStart);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(8, editor.SelectionStart);
        previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, editor.SelectionStart);
        previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(8, editor.SelectionStart);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, editor.SelectionStart);
        find.Focus();
        find.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.True(find.IsFocused);
        Assert.Equal(8, editor.SelectionStart);
        find.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(0, editor.SelectionStart);

        editor.Text = "Итого\nСчёт 30";
        Assert.Equal("Найдено: 1", count.Text);
        queue.SelectedIndex = 1;
        Assert.Equal("Найдено: 2", count.Text);
        find.Text = "не найдено";
        Assert.Equal("0", count.Text);
        Assert.False(next.IsEnabled);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task RecognitionFailure_DoesNotExposeExceptionOrPretendSuccess()
    {
        var window = CreateWindow(new FakeRuntime { Failure = new IOException("secret/path/customer-text") });
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("source.png")]);
        await window.RecognizeQueueAsync();
        var status = Find<TextBlock>(window, "TextStatus").Text!;
        Assert.Contains("Не удалось", status);
        Assert.DoesNotContain("secret", status);
        Assert.True(Find<Button>(window, "TextRecognize").IsEnabled);
        Assert.False(Find<Button>(window, "TextExport").IsEnabled);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task RecognitionProgress_TracksCompletedDocumentsAndCurrentActivity()
    {
        var runtime = new FakeRuntime { HoldOnCall = 2 };
        var window = CreateWindow(runtime);
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("one.png", "one"), await Source("two.png", "two")]);
        var progress = Find<ProgressBar>(window, "TextQueueProgress");
        var progressPanel = Find<StackPanel>(window, "TextRecognitionProgress");
        Assert.False(progressPanel.IsVisible);

        var operation = window.RecognizeQueueAsync();
        await runtime.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.UpdateLayout();
        Assert.True(progressPanel.IsVisible);
        Assert.True(progress.Bounds.Width > 100);
        Assert.True(progress.Bounds.Height > 0);
        Assert.Equal(2, progress.Maximum);
        Assert.Equal(1, progress.Value);
        Assert.Equal("Обработано 1 из 2", Find<TextBlock>(window, "TextProgressLabel").Text);
        Assert.True(Find<ProgressBar>(window, "TextOcrActivity").IsIndeterminate);
        Assert.Contains("2/2", Find<TextBlock>(window, "TextStatus").Text);

        runtime.ReleaseHeld.SetResult();
        await operation;
        Assert.False(progressPanel.IsVisible);
        Assert.Contains("завершено", Find<TextBlock>(window, "TextStatus").Text);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task RecognitionProgress_CancellationDoesNotCountCurrentDocument()
    {
        var runtime = new FakeRuntime { WaitForCancellation = true };
        var window = CreateWindow(runtime);
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("one.png")]);

        var operation = window.RecognizeQueueAsync();
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Find<StackPanel>(window, "TextRecognitionProgress").IsVisible);
        Find<Button>(window, "TextCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await operation;

        Assert.False(Find<StackPanel>(window, "TextRecognitionProgress").IsVisible);
        Assert.Equal(0, Find<ProgressBar>(window, "TextQueueProgress").Value);
        Assert.Contains("отменена", Find<TextBlock>(window, "TextStatus").Text);
        Assert.True(Find<Button>(window, "TextRecognize").IsEnabled);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task ReviewBlocks_SelectTextAndHighlightImage_ThenFilterUncertainLines()
    {
        var extraction = new ExtractionEnvelope("image", "test", "1", [
            new ExtractionPage(1, 200, 100, [
                Block("first", "Первая строка", 0.95, 20, 10, 90, 30),
                Block("second", "Вторая строка", 0.55, 20, 40, 100, 60),
            ], [], CoordinateFrame: "original-pixels"),
        ], [], [], []);
        var window = CreateWindow(new FakeRuntime { Result = extraction });
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([ImageSource("source.png")]);
        await window.RecognizeQueueAsync();
        window.UpdateLayout();

        var sections = Find<Expander>(window, "TextReviewSections");
        Assert.True(sections.IsVisible);
        sections.IsExpanded = true;
        window.UpdateLayout();
        var lines = Find<ListBox>(window, "TextReviewBlocks");
        Assert.Equal(2, lines.ItemCount);
        lines.SelectedIndex = 1;
        var editor = Find<TextBox>(window, "TextEditor");
        Assert.Equal("Вторая строка", editor.Text![editor.SelectionStart..editor.SelectionEnd]);
        var preview = Find<Image>(window, "TextSourcePreview");
        Assert.True(preview.Source is not null && preview.Bounds.Width > 0 && preview.Bounds.Height > 0,
            $"Preview source={preview.Source is not null}, bounds={preview.Bounds}, status={Find<TextBlock>(window, "TextStatus").Text}");
        Assert.Contains("и на странице", Find<TextBlock>(window, "TextStatus").Text);
        var highlight = Find<Canvas>(window, "TextSourceHighlight");
        var expected = ReviewLinking.HighlightRect(extraction.Pages[0], extraction.Pages[0].TextBlocks[1],
            new Avalonia.Size(200, 100), highlight.Bounds.Size);
        Assert.NotNull(expected);
        var marker = Assert.Single(highlight.Children);
        Assert.Equal(expected.Value.X, Canvas.GetLeft(marker), 3);
        Assert.Equal(expected.Value.Y, Canvas.GetTop(marker), 3);

        Find<CheckBox>(window, "TextUncertainOnly").IsChecked = true;
        Assert.Equal(1, lines.ItemCount);
        Assert.Empty(Find<Canvas>(window, "TextSourceHighlight").Children);
        lines.SelectedIndex = 0;
        Assert.Single(Find<Canvas>(window, "TextSourceHighlight").Children);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task ReviewBlocks_PdfPageSwitchClearsSelection_AndTextLayerNeverHighlights()
    {
        var artifactRoot = Path.Combine(_root, "workspace", "ocr-runs");
        Directory.CreateDirectory(artifactRoot);
        var pageOne = ImageSource("page-0001.png", artifactRoot);
        var pageTwo = ImageSource("page-0002.png", artifactRoot);
        var extraction = new ExtractionEnvelope("pdf", "test", "1", [
            new ExtractionPage(1, 200, 100, [Block("scan", "Скан", 0.9, 20, 10, 90, 30)], [], CoordinateFrame: "original-pixels"),
            new ExtractionPage(2, 200, 100, [Block("text-layer-0002", "Текст PDF", 1.0, 20, 10, 90, 30)], []),
        ], [], [], [pageOne, pageTwo]);
        var window = CreateWindow(new FakeRuntime { Result = extraction });
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("source.pdf", "%PDF-test")]);
        await window.RecognizeQueueAsync();
        window.UpdateLayout();
        Find<Expander>(window, "TextReviewSections").IsExpanded = true;
        var lines = Find<ListBox>(window, "TextReviewBlocks");
        lines.SelectedIndex = 0;
        Assert.Single(Find<Canvas>(window, "TextSourceHighlight").Children);

        Find<Button>(window, "TextNextPage").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Assert.Equal(-1, lines.SelectedIndex);
        Assert.Empty(Find<Canvas>(window, "TextSourceHighlight").Children);
        lines.SelectedIndex = 0;
        Assert.Empty(Find<Canvas>(window, "TextSourceHighlight").Children);
        Assert.Contains("Точное положение", Find<TextBlock>(window, "TextStatus").Text);
        var editor = Find<TextBox>(window, "TextEditor");
        Assert.Equal("Текст PDF", editor.Text![editor.SelectionStart..editor.SelectionEnd]);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task UncertainReview_NavigatesPagesPersistsMarksAndConfirmsRemainingCount()
    {
        var artifactRoot = Path.Combine(_root, "workspace", "ocr-runs");
        Directory.CreateDirectory(artifactRoot);
        var pageOne = ImageSource("page-0001.png", artifactRoot);
        var pageTwo = ImageSource("page-0002.png", artifactRoot);
        var extraction = new ExtractionEnvelope("review", "test", "1", [
            new ExtractionPage(1, 200, 100, [Block("a", "Повтор", 0.5, 20, 10, 90, 30)], []),
            new ExtractionPage(2, 200, 100, [Block("b", "Повтор", 0.4, 20, 40, 90, 60)], []),
        ], [], [], [pageOne, pageTwo]);
        var window = CreateWindow(new FakeRuntime { Result = extraction });
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("review.pdf", "%PDF-test-review")]);
        await window.RecognizeQueueAsync();
        Find<Expander>(window, "TextReviewSections").IsExpanded = true;
        window.UpdateLayout();

        var next = Find<Button>(window, "TextNextUncertain");
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("1/2", Find<TextBlock>(window, "TextPageStatus").Text);
        Assert.Equal(0, Find<ListBox>(window, "TextReviewBlocks").SelectedIndex);
        Assert.Empty(Find<Canvas>(window, "TextSourceHighlight").Children);
        var mark = Find<CheckBox>(window, "TextBlockReviewed");
        mark.IsChecked = true;
        Assert.Contains("Осталось: 1", Find<TextBlock>(window, "TextUncertainCount").Text);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("2/2", Find<TextBlock>(window, "TextPageStatus").Text);
        Assert.Equal(0, Find<ListBox>(window, "TextReviewBlocks").SelectedIndex);
        Assert.Empty(Find<Canvas>(window, "TextSourceHighlight").Children);

        await window.FlushAndCloseAsync();
        var reopened = CreateWindow(new FakeRuntime());
        reopened.Show(); await reopened.InitializeAsync();
        Find<Expander>(reopened, "TextReviewSections").IsExpanded = true;
        reopened.UpdateLayout();
        Assert.Contains("Осталось: 1", Find<TextBlock>(reopened, "TextUncertainCount").Text);
        await reopened.ApproveSelectedAsync();
        Assert.Contains("Осталось непроверенных сомнительных строк: 1", Find<TextBlock>(reopened, "TextStatus").Text);
        Assert.Equal("Подтвердить с остатком", Find<Button>(reopened, "TextApprove").Content);
        await reopened.ApproveSelectedAsync();
        Assert.Contains("Непроверенных сомнительных строк: 1", Find<TextBlock>(reopened, "TextStatus").Text);
        Assert.True(Find<Button>(reopened, "TextExport").IsEnabled);
        await reopened.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task UncertainReview_NavigatesFromAnEmptyPdfPage()
    {
        var artifactRoot = Path.Combine(_root, "workspace", "ocr-runs");
        Directory.CreateDirectory(artifactRoot);
        var extraction = new ExtractionEnvelope("empty-page", "test", "1", [
            new ExtractionPage(1, 200, 100, [], []),
            new ExtractionPage(2, 200, 100, [Block("line", "Следующая страница", 0.4, 20, 40, 90, 60)], []),
        ], [], [], [ImageSource("page-0001.png", artifactRoot), ImageSource("page-0002.png", artifactRoot)]);
        var window = CreateWindow(new FakeRuntime { Result = extraction });
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("empty-page.pdf", "%PDF-test-empty-page")]);
        await window.RecognizeQueueAsync();
        var sections = Find<Expander>(window, "TextReviewSections");
        Assert.True(sections.IsVisible);
        sections.IsExpanded = true;
        window.UpdateLayout();
        Find<Button>(window, "TextNextUncertain").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("2/2", Find<TextBlock>(window, "TextPageStatus").Text);
        Assert.Equal(0, Find<ListBox>(window, "TextReviewBlocks").SelectedIndex);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task ReferenceSave_RequiresApprovalAndCreatesLocalSample()
    {
        var window = CreateWindow(new FakeRuntime { Text = "Исходный текст" });
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("reference.png", "image bytes")]);
        await window.RecognizeQueueAsync();
        var button = Find<Button>(window, "TextSaveReference");
        Assert.False(button.IsEnabled);

        Find<TextBox>(window, "TextEditor").Text = "Проверенный текст";
        await window.ApproveSelectedAsync();
        Assert.True(button.IsEnabled);
        await window.SaveReferenceSelectedAsync();
        Assert.Contains("Эталон сохранён локально", Find<TextBlock>(window, "TextStatus").Text);
        var manifest = Assert.Single(Directory.GetFiles(Path.Combine(_root, "workspace", "quality-lab", "samples"), "*.json"));
        Assert.Contains("referenceSha256", await File.ReadAllTextAsync(manifest));

        Find<TextBox>(window, "TextEditor").Text = "Новая правка";
        Assert.False(button.IsEnabled);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task CorruptWorkspace_BlocksChangesAndPreservesBytes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        var state = Path.Combine(_root, "workspace", "workspace.json");
        await File.WriteAllTextAsync(state, "{not-valid-json}");
        var window = CreateWindow(new FakeRuntime());
        window.Show(); await window.InitializeAsync();
        Assert.False(Find<Button>(window, "TextAddFiles").IsEnabled);
        await window.FlushAndCloseAsync();
        Assert.Equal("{not-valid-json}", await File.ReadAllTextAsync(state));
    }

    [AvaloniaFact]
    public async Task Statistics_RequireExplicitGrantAndRevocationClearsQueue()
    {
        var window = CreateWindow(new FakeRuntime());
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("one.png", "one")]);
        await window.RecognizeQueueAsync();
        var stats = new LocalUsageStatisticsOutbox(Path.Combine(_root, "workspace"));
        Assert.Equal(0, stats.PendingCount);
        await window.SetStatisticsConsentAsync(true);
        await window.ImportPathsAsync([await Source("two.png", "two")]);
        await window.RecognizeQueueAsync();
        Assert.Equal(1, stats.PendingCount);
        await window.SetStatisticsConsentAsync(false);
        Assert.Equal(0, stats.PendingCount);
        Assert.False(File.Exists(stats.QueuePath));
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task PrivacyDialog_ShowsCompactChoices()
    {
        var window = CreateWindow(new FakeRuntime());
        window.Show(); await window.InitializeAsync();
        var dialog = new PrivacyDialog(false, window.SetStatisticsConsentAsync);
        dialog.Show(window);
        var checkboxes = dialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
        Assert.Equal(2, checkboxes.Length);
        var statistics = Find<CheckBox>(dialog, "TextStatisticsConsent");
        Assert.Equal("TextStatisticsConsent", AutomationProperties.GetAutomationId(statistics));
        Assert.True(statistics.IsEnabled);
        Assert.False(statistics.IsChecked);
        var examples = Find<CheckBox>(dialog, "TextExamplesConsent");
        Assert.False(examples.IsEnabled);
        Assert.False(examples.IsChecked);
        Assert.NotNull(dialog.Icon);
        Assert.Equal(SizeToContent.Height, dialog.SizeToContent);
        Assert.False(dialog.CanResize);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Text?.Contains("Ничего не отправляется", StringComparison.Ordinal) == true);
        Assert.True(Find<Button>(dialog, "PrivacySave").IsEnabled);
        Assert.True(Find<Button>(dialog, "PrivacyCancel").IsEnabled);
        dialog.UpdateLayout();
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<ScrollViewer>(), _ => true);
        Assert.All(((Grid)dialog.Content!).RowDefinitions, row => Assert.Equal(GridLength.Auto, row.Height));

        dialog.Close();
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task PrivacyDialog_SavesTheSelectedLocalChoice()
    {
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new PrivacyDialog(false, enabled =>
        {
            saved.SetResult(enabled);
            return Task.CompletedTask;
        });
        dialog.Show();
        Find<CheckBox>(dialog, "TextStatisticsConsent").IsChecked = true;
        Find<Button>(dialog, "PrivacySave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await saved.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public async Task PrivacyDialog_ShowsSaveFailureWithoutClosing()
    {
        var dialog = new PrivacyDialog(false, _ => Task.FromException(new IOException("private/path")));
        dialog.Show();
        Find<Button>(dialog, "PrivacySave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Yield();
        var message = Find<TextBlock>(dialog, "PrivacySaveError");
        Assert.True(dialog.IsVisible);
        Assert.True(message.IsVisible);
        Assert.DoesNotContain("private/path", message.Text);
        Assert.True(Find<Button>(dialog, "PrivacySave").IsEnabled);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task LocalRuntime_DisablesNetworkBootstrapAndTransportProcessing()
    {
        var options = LocalTextRuntime.CreateOptions(_root);
        Assert.False(options.AutoBootstrapPythonEnvironment);
        Assert.True(options.TextOnly);
        Assert.True(options.OfflineOnly);
        Assert.True(options.RetryTextWithoutUnwarpingOnMemoryLimit);
        Assert.Equal("paddle", options.PreferredEngine);
        Assert.Equal(20, options.MaximumPages);
        Assert.Equal(20_000_000, options.MaximumPagePixels);
        Assert.Equal(2L * 1024 * 1024 * 1024, options.MaximumProcessMemoryBytes);
        Assert.Equal(TimeSpan.FromSeconds(300), options.ExtractTimeout);
        Assert.True(Path.IsPathFullyQualified(options.PythonExecutablePath));
        Assert.Equal("rus+eng", options.TesseractLanguages);
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public void PackagedRuntime_DoesNotUseSystemPythonOrDeveloperCache()
    {
        var sidecar = Path.Combine(_root, "package", "tools", "ocr-sidecar");
        var privatePython = Path.Combine(sidecar, ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(privatePython)!);
        File.WriteAllText(privatePython, "not-a-packaged-runtime");
        var options = LocalTextRuntime.CreateOptions(_root, Path.Combine(_root, "package"));
        Assert.NotEqual(privatePython, options.PythonExecutablePath);
        Assert.Equal(Path.Combine(_root, "package", "tools", "python310", "python.exe"), options.PythonExecutablePath);
        Assert.Equal(Path.Combine(sidecar, "models", "paddlex"), options.PaddleModelCacheHome);
        Assert.Null(options.SidecarExecutablePath);
    }

    [Theory]
    [InlineData(4000, 5000, true)]
    [InlineData(4000, 5001, false)]
    [InlineData(int.MaxValue, int.MaxValue, false)]
    [InlineData(0, 100, false)]
    [InlineData(-1, 100, false)]
    public void PreviewBudget_IsBoundedBeforeBitmapAllocation(int width, int height, bool accepted) =>
        Assert.Equal(accepted, LocalTextPreview.IsWithinBudget(width, height));

    [Theory]
    [InlineData("OFFLINE_MODELS_MISSING", LocalTextFailure.ModelsMissing)]
    [InlineData("TEXT_ONLY_RUNTIME_MISSING", LocalTextFailure.RuntimeMissing)]
    [InlineData("TEXT_ONLY_SOURCE_LIMIT", LocalTextFailure.SourceLimit)]
    [InlineData("TEXT_ONLY_PAGE_LIMIT", LocalTextFailure.PageLimit)]
    [InlineData("TEXT_ONLY_PIXEL_LIMIT", LocalTextFailure.PixelLimit)]
    [InlineData("TEXT_ONLY_MEMORY_LIMIT", LocalTextFailure.MemoryLimit)]
    [InlineData("TEXT_ONLY_TIMEOUT", LocalTextFailure.Timeout)]
    [InlineData("TEXT_ONLY_SOURCE_INVALID", LocalTextFailure.InvalidSource)]
    [InlineData("TEXT_ONLY_RESOURCE_GUARD_FAILED", LocalTextFailure.ResourceGuard)]
    [InlineData("TEXT_ONLY_OUTPUT_LIMIT", LocalTextFailure.OutputLimit)]
    [InlineData("secret/path/text", LocalTextFailure.Unknown)]
    public void RuntimeFailure_UsesOnlyAllowlistedCategory(string code, LocalTextFailure expected)
    {
        var failure = LocalTextFailureMessages.Classify(new LocalOcrRuntimeException(code));
        Assert.Equal(expected, failure);
        var message = LocalTextFailureMessages.Describe(failure);
        Assert.DoesNotContain("secret", message);
        Assert.DoesNotContain(code, message);
    }

    [AvaloniaFact]
    public async Task MissingModels_DoesNotSuggestServerLoginOrDownload()
    {
        var window = CreateWindow(new FakeRuntime { Failure = new LocalOcrRuntimeException("OFFLINE_MODELS_MISSING") });
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("source.png")]);
        await window.RecognizeQueueAsync();
        var status = Find<TextBlock>(window, "TextStatus").Text!;
        Assert.Contains("локальные модели", status);
        Assert.Contains("скачивание моделей не выполнялось", status);
        Assert.DoesNotContain("интернет", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("логин", status, StringComparison.OrdinalIgnoreCase);
        Assert.True(Find<Button>(window, "TextRecognize").IsEnabled);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task EmptyRecognition_KeepsRetryEnabledAndSuccessfulRetryClearsFailure()
    {
        var runtime = new FakeRuntime { Text = " " };
        var window = CreateWindow(runtime);
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("source.png")]);
        await window.RecognizeQueueAsync();
        Assert.Contains("Текст не найден", Find<TextBlock>(window, "TextStatus").Text);
        Assert.True(Find<Button>(window, "TextRecognize").IsEnabled);
        Assert.False(Find<Button>(window, "TextApprove").IsEnabled);
        runtime.Text = "Повторная попытка успешна";
        await window.RecognizeQueueAsync();
        Assert.Equal(runtime.Text, Find<TextBox>(window, "TextEditor").Text);
        Assert.False(Find<Button>(window, "TextRecognize").IsEnabled);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task NewOperationError_IsNotReplacedBySelectedDocumentsOldFailure()
    {
        var window = CreateWindow(new FakeRuntime { Failure = new LocalOcrRuntimeException("OFFLINE_MODELS_MISSING") });
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("source.png")]);
        await window.RecognizeQueueAsync();
        Assert.Contains("локальные модели", Find<TextBlock>(window, "TextStatus").Text);
        await window.ImportPathsAsync([Path.Combine(_root, "unsupported.exe")]);
        Assert.Contains("не удалось 1", Find<TextBlock>(window, "TextStatus").Text);
        var failures = (ListBox)Find<Expander>(window, "TextImportFailures").Content!;
        Assert.Contains("Неподдерживаемый формат", Assert.Single(failures.ItemsSource!.Cast<string>()));
        Assert.DoesNotContain("локальные модели", Find<TextBlock>(window, "TextStatus").Text);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task Timeout_IsActionableAndSurvivesRestartWithoutRawError()
    {
        var window = CreateWindow(new FakeRuntime { Failure = new TimeoutException("secret/path/text") });
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("source.png")]);
        await window.RecognizeQueueAsync();
        Assert.Contains("5 минут", Find<TextBlock>(window, "TextStatus").Text);
        Assert.DoesNotContain("secret", Find<TextBlock>(window, "TextStatus").Text);
        await window.FlushAndCloseAsync();
        var reopened = CreateWindow(new FakeRuntime());
        reopened.Show(); await reopened.InitializeAsync();
        Assert.Contains("5 минут", Find<TextBlock>(reopened, "TextStatus").Text);
        await reopened.RecognizeQueueAsync();
        Assert.Equal("Распознанный текст", Find<TextBox>(reopened, "TextEditor").Text);
        await reopened.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task OversizedImport_ExplainsLocalLimitWithoutChangingSource()
    {
        var window = CreateWindow(new FakeRuntime());
        window.Show(); await window.InitializeAsync();
        var path = await Source("large.pdf");
        using (var source = File.OpenWrite(path)) source.SetLength(LocalTextWorkspace.MaximumSourceBytes + 1);
        await window.ImportPathsAsync([path]);
        Assert.Contains("20 МиБ", Find<TextBlock>(window, "TextStatus").Text);
        Assert.False(Find<Button>(window, "TextRecognize").IsEnabled);
        Assert.Equal(LocalTextWorkspace.MaximumSourceBytes + 1, new FileInfo(path).Length);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task BatchImport_ContinuesAfterFileErrorsAndReportsDuplicatesAndReasons()
    {
        var window = CreateWindow(new FakeRuntime());
        window.Show(); await window.InitializeAsync();
        var first = await Source("first.png", "same-image");
        var duplicate = await Source("duplicate.jpg", "same-image");
        var oversized = await Source("large.pdf");
        using (var source = File.OpenWrite(oversized)) source.SetLength(LocalTextWorkspace.MaximumSourceBytes + 1);
        var last = await Source("last.png", "other-image");
        await window.ImportPathsAsync([
            first,
            Path.Combine(_root, "missing.png"),
            duplicate,
            oversized,
            Path.Combine(_root, "unsupported.exe"),
            last,
        ]);

        Assert.Equal("Импорт завершён: добавлено 2, дубликатов 1, не удалось 3.", Find<TextBlock>(window, "TextStatus").Text);
        Assert.Equal(2, Find<ListBox>(window, "TextDocumentQueue").ItemCount);
        Assert.True(Find<Button>(window, "TextRecognize").IsEnabled);
        var details = Find<Expander>(window, "TextImportFailures");
        Assert.True(details.IsVisible);
        Assert.True(details.IsExpanded);
        var failures = ((ListBox)details.Content!).ItemsSource!.Cast<string>().ToArray();
        Assert.Equal(3, failures.Length);
        Assert.Contains(failures, item => item.Contains("missing.png") && item.Contains("не найден"));
        Assert.Contains(failures, item => item.Contains("large.pdf") && item.Contains("20 МиБ"));
        Assert.Contains(failures, item => item.Contains("unsupported.exe") && item.Contains("Неподдерживаемый формат"));
        await window.ImportPathsAsync([first]);
        Assert.Contains("дубликатов 1, не удалось 0", Find<TextBlock>(window, "TextStatus").Text);
        Assert.False(details.IsVisible);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task StatisticsStorageFailure_DoesNotBlockLocalRecognition()
    {
        var window = CreateWindow(new FakeRuntime());
        var store = new LocalLearningConsentStore(Path.Combine(_root, "workspace"));
        Directory.CreateDirectory(Path.GetDirectoryName(store.StorageRootPath)!);
        await File.WriteAllTextAsync(store.StorageRootPath, "storage-is-not-a-directory");
        window.Show(); await window.InitializeAsync();
        Assert.True(Find<Button>(window, "TextAddFiles").IsEnabled);
        await window.ImportPathsAsync([await Source("one.png")]);
        await window.RecognizeQueueAsync();
        Assert.Equal("Распознанный текст", Find<TextBox>(window, "TextEditor").Text);
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task Startup_PrunesExpiredStatisticsWithoutFurtherRecognition()
    {
        var window = CreateWindow(new FakeRuntime());
        var root = Path.Combine(_root, "workspace");
        var consent = new LocalLearningConsent(Statistics: true);
        await new LocalLearningConsentStore(root).SaveAsync(consent);
        var oldQueue = new LocalUsageStatisticsOutbox(root, new OldTimeProvider());
        await oldQueue.RecordRecognitionAsync(consent, 1, 1, TimeSpan.FromSeconds(1), LocalRecognitionOutcome.Completed);
        Assert.True(File.Exists(oldQueue.QueuePath));
        window.Show(); await window.InitializeAsync();
        Assert.False(File.Exists(oldQueue.QueuePath));
        await window.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public void SecondInstance_ShowsStorageErrorWithoutModifyingExistingWorkspace()
    {
        var root = Path.Combine(_root, "workspace");
        using var lease = new LocalTextWorkspace(root);
        var window = LocalTextWindow.CreateStartupWindow(() => new LocalTextWorkspace(root));
        Assert.IsNotType<LocalTextWindow>(window);
        window.Show();
        Assert.Contains("уже запущена", Assert.IsType<TextBlock>(window.Content).Text);
        Assert.False(File.Exists(Path.Combine(root, "workspace.json")));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClosingDuringRecognition_CancelsAndRestoresRetryableQueue()
    {
        var runtime = new FakeRuntime { WaitForCancellation = true };
        var window = CreateWindow(runtime);
        window.Show(); await window.InitializeAsync();
        await window.ImportPathsAsync([await Source("one.png")]);
        var operation = window.RecognizeQueueAsync();
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Find<StackPanel>(window, "TextRecognitionProgress").IsVisible);
        var closing = window.FlushAndCloseAsync();
        Assert.False(Find<TextBox>(window, "TextEditor").IsEnabled);
        Assert.False(Find<ListBox>(window, "TextDocumentQueue").IsEnabled);
        await closing; await operation;
        var reopened = CreateWindow(new FakeRuntime());
        reopened.Show(); await reopened.InitializeAsync();
        Assert.True(Find<Button>(reopened, "TextRecognize").IsEnabled);
        await reopened.RecognizeQueueAsync();
        Assert.Equal("Распознанный текст", Find<TextBox>(reopened, "TextEditor").Text);
        await reopened.FlushAndCloseAsync();
    }

    [AvaloniaFact]
    public async Task DuplicateImport_PersistsPendingEditsBeforeStoppingAutosave()
    {
        var window = CreateWindow(new FakeRuntime());
        window.Show(); await window.InitializeAsync();
        var source = await Source("one.png");
        await window.ImportPathsAsync([source]); await window.RecognizeQueueAsync();
        Find<TextBox>(window, "TextEditor").Text = "Последняя правка";
        await window.ImportPathsAsync([source]);
        using var state = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "workspace", "workspace.json")));
        Assert.Equal("Последняя правка", state.RootElement.GetProperty("documents")[0].GetProperty("editedText").GetString());
        Assert.Equal("Последняя правка", Find<TextBox>(window, "TextEditor").Text);
        await window.FlushAndCloseAsync();
    }

    private sealed class OldTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(-8);
    }

    private LocalTextWindow CreateWindow(IOcrRuntimeClient runtime) => new(new LocalTextWorkspace(Path.Combine(_root, "workspace")), runtime, initializeOnOpen: false);

    private async Task<string> Source(string name, string content = "synthetic-source")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private async Task<string> HoldoutSourceAsync(int index)
    {
        for (var attempt = 0; ; attempt++)
        {
            var content = $"quality-source-{index}-{attempt}";
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            if (LocalTextQualityLab.SplitForSource(hash) == "holdout")
                return await Source($"quality-{index}.png", content);
        }
    }

    private string ImageSource(string name, string? folder = null)
    {
        var directory = folder ?? _root;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        using var bitmap = new SKBitmap(200, 100);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private static ExtractionTextBlock Block(string id, string text, double confidence, double left, double top, double right, double bottom) =>
        new(id, text, confidence, [new Point2D(left, top), new Point2D(right, top), new Point2D(right, bottom), new Point2D(left, bottom)]);

    private static T Find<T>(Window window, string id) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private sealed class FakeRuntime : IOcrRuntimeClient
    {
        private int _calls;
        public Exception? Failure { get; init; }
        public bool WaitForCancellation { get; init; }
        public ExtractionEnvelope? Result { get; init; }
        public int HoldOnCall { get; init; }
        public string Text { get; set; } = "Распознанный текст";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OcrRuntimeHealth> HealthcheckAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No server or readiness probe is required.");
        public async Task<ExtractionEnvelope> ExtractAsync(string inputPath, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            Started.TrySetResult();
            if (Interlocked.Increment(ref _calls) == HoldOnCall)
            {
                HeldStarted.TrySetResult();
                await ReleaseHeld.Task.WaitAsync(cancellationToken);
            }
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Result ?? new ExtractionEnvelope("local", "local", "1", [
                new ExtractionPage(1, 100, 100, [new ExtractionTextBlock("1", Text, 0.9, [])], [])
            ], [], [], []);
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}

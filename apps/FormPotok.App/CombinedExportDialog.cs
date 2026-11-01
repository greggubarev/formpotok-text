using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FormPotok.PilotCore.LocalText;

namespace FormPotok.App;

internal sealed class CombinedExportDialog : Window
{
    private readonly List<LocalTextDocument> _documents;
    private readonly HashSet<Guid> _included;
    private readonly ListBox _list = new() { Height = 250 };
    private readonly CheckBox _headers = new() { Content = "Добавить заголовок перед каждым документом", IsChecked = true };
    private readonly Button _save = new() { Content = "Продолжить", MinWidth = 100 };
    private readonly TextBlock _summary = new();

    public CombinedExportDialog(IEnumerable<LocalTextDocument> documents)
    {
        _documents = documents.Where(document => document.Status == LocalTextStatus.Reviewed).ToList();
        _included = _documents.Select(document => document.Id).ToHashSet();
        Title = "Общий TXT";
        Icon = BrandAssets.CreateWindowIcon();
        Width = 550; MinWidth = 440; Height = 450; MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        AutomationProperties.SetAutomationId(_list, "CombinedExportDocuments");
        AutomationProperties.SetAutomationId(_headers, "CombinedExportHeaders");
        AutomationProperties.SetAutomationId(_save, "CombinedExportContinue");
        var up = new Button { Content = "↑", Width = 36, Height = 32 };
        var down = new Button { Content = "↓", Width = 36, Height = 32 };
        AutomationProperties.SetName(up, "Переместить выше");
        AutomationProperties.SetName(down, "Переместить ниже");
        AutomationProperties.SetAutomationId(up, "CombinedExportUp");
        AutomationProperties.SetAutomationId(down, "CombinedExportDown");
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        _save.Click += (_, _) => Close(new CombinedExportSelection(SelectedIds(), _headers.IsChecked == true));
        var cancel = new Button { Content = "Отмена", MinWidth = 85 };
        cancel.Click += (_, _) => Close();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Avalonia.Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Документы в общем файле", FontSize = 19, FontWeight = FontWeight.SemiBold });
        var listRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(0, 12, 0, 8) };
        listRow.Children.Add(_list);
        var arrows = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(8, 0, 0, 0), Children = { up, down } };
        Grid.SetColumn(arrows, 1); listRow.Children.Add(arrows);
        Grid.SetRow(listRow, 1); root.Children.Add(listRow);
        Grid.SetRow(_summary, 2); root.Children.Add(_summary);
        _headers.Margin = new Avalonia.Thickness(0, 12, 0, 12);
        Grid.SetRow(_headers, 3); root.Children.Add(_headers);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, _save } };
        Grid.SetRow(buttons, 4); root.Children.Add(buttons);
        Content = root;
        Refresh();
    }

    private Guid[] SelectedIds() => _documents.Where(document => _included.Contains(document.Id)).Select(document => document.Id).ToArray();

    private void Refresh(Guid? selectedId = null)
    {
        _list.ItemsSource = _documents.Select(document => document).ToArray();
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LocalTextDocument>((document, _) =>
        {
            var check = new CheckBox { Content = document?.DisplayName, IsChecked = document is not null && _included.Contains(document.Id), Margin = new Avalonia.Thickness(4) };
            if (document is not null) check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) _included.Add(document.Id);
                else _included.Remove(document.Id);
                UpdateSummary();
            };
            return check;
        });
        if (selectedId is { } id) _list.SelectedItem = _documents.FirstOrDefault(document => document.Id == id);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        _summary.Text = $"Выбрано: {_included.Count} из {_documents.Count}";
        _save.IsEnabled = _included.Count > 0;
    }

    private void Move(int offset)
    {
        if (_list.SelectedItem is not LocalTextDocument selected) return;
        var index = _documents.FindIndex(document => document.Id == selected.Id);
        var next = index + offset;
        if (next < 0 || next >= _documents.Count) return;
        (_documents[index], _documents[next]) = (_documents[next], _documents[index]);
        Refresh(selected.Id);
    }
}

internal sealed record CombinedExportSelection(IReadOnlyList<Guid> DocumentIds, bool IncludeHeaders);

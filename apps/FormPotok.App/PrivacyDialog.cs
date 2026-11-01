using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FormPotok.App;

internal sealed class PrivacyDialog : Window
{
    private readonly CheckBox _statistics;
    private readonly Button _save;
    private readonly TextBlock _error;

    public PrivacyDialog(bool statisticsEnabled, Func<bool, Task> saveConsentAsync)
    {
        Title = "Данные и конфиденциальность";
        Icon = BrandAssets.CreateWindowIcon();
        Width = 520;
        MinWidth = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        _statistics = new CheckBox
        {
            Content = "Сохранять техническую статистику",
            IsChecked = statisticsEnabled,
            FontSize = 14,
        };
        AutomationProperties.SetAutomationId(_statistics, "TextStatisticsConsent");
        AutomationProperties.SetName(_statistics, "Сохранять техническую статистику на этом компьютере");
        var examples = new CheckBox
        {
            Content = "Передавать примеры для обучения",
            IsChecked = false,
            IsEnabled = false,
            FontSize = 14,
        };
        AutomationProperties.SetAutomationId(examples, "TextExamplesConsent");

        _error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Firebrick,
            FontSize = 12,
            IsVisible = false,
        };
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        AutomationProperties.SetAutomationId(_error, "PrivacySaveError");

        _save = new Button { Content = "Сохранить", MinWidth = 96 };
        AutomationProperties.SetAutomationId(_save, "PrivacySave");
        var cancel = new Button { Content = "Отмена", MinWidth = 88 };
        AutomationProperties.SetAutomationId(cancel, "PrivacyCancel");
        cancel.Click += (_, _) => Close();
        _save.Click += async (_, _) =>
        {
            _save.IsEnabled = false;
            _error.IsVisible = false;
            try
            {
                await saveConsentAsync(_statistics.IsChecked == true);
                Close();
            }
            catch
            {
                _error.Text = "Не удалось сохранить настройку. Попробуйте ещё раз.";
                _error.IsVisible = true;
                _save.IsEnabled = true;
            }
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            Margin = new Avalonia.Thickness(24, 18),
        };
        root.Children.Add(new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock { Text = "Ваши данные", FontSize = 22, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Документы, распознанный текст и правки остаются на этом компьютере.",
                    FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
                },
            },
        });

        var details = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(0, 18, 0, 16) };
        details.Children.Add(new Border { Height = 1, Background = Brushes.LightGray });
        details.Children.Add(_statistics);
        details.Children.Add(new TextBlock
        {
            Text = "Версия программы, результат обработки, количество страниц и время. Без текста, изображений и имён файлов.",
            TextWrapping = TextWrapping.Wrap, FontSize = 13,
        });
        details.Children.Add(new TextBlock
        {
            Text = "Ничего не отправляется. Записи старше 7 дней удаляются при следующем запуске; выключение статистики удаляет их сразу.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DimGray,
        });
        details.Children.Add(new Border { Height = 1, Background = Brushes.LightGray, Margin = new Avalonia.Thickness(0, 6, 0, 2) });
        details.Children.Add(examples);
        details.Children.Add(new TextBlock
        {
            Text = "Передача примеров пока недоступна.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DimGray,
        });
        Grid.SetRow(details, 1);
        root.Children.Add(details);

        var footer = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        footer.Children.Add(_error);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { cancel, _save },
        };
        Grid.SetRow(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }
}

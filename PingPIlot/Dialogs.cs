using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Panel = System.Windows.Controls.Panel;
using PingPilot.Models;

namespace PingPilot;

/// <summary>Небольшие диалоги, собранные в коде.</summary>
public static class Dialogs
{
    public static string? AskGroupName(Window owner, string? initialName = null)
    {
        var name = new TextBox { Text = initialName ?? string.Empty };

        bool ok = Show(owner, initialName == null ? "Новая группа" : "Изменить группу",
            "Сохранить",
            new[] { ("Название группы, например «Кабинет 101»", (Control)name) },
            () => name.Text.Trim().Length == 0 ? "Введите название группы." : null);

        return ok ? name.Text.Trim() : null;
    }

    /// <summary>
    /// Окно добавления компьютеров «подряд»: Enter или «Добавить» сохраняет компьютер
    /// и очищает поля, окно остаётся открытым. «Добавить и закрыть» сохраняет и закрывает.
    /// tryAdd возвращает null при успехе или текст ошибки (например, дубль).
    /// </summary>
    public static void AddComputersContinuously(
        Window owner,
        IReadOnlyList<ComputerGroup> groups,
        Guid? selectedGroupId,
        Func<SavedComputer, string?> tryAdd)
    {
        var host = new TextBox();
        var description = new TextBox();
        ComboBox group = GroupCombo(groups, selectedGroupId);
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 36,
            Margin = new Thickness(0, 0, 0, 8),
            VerticalAlignment = VerticalAlignment.Bottom
        };

        var root = new StackPanel { Margin = new Thickness(24) };
        AddField(root, "IP-адрес или DNS-имя (Enter — добавить и продолжить)", host);
        AddField(root, "Описание (необязательно)", description);
        AddField(root, "Группа", group);
        root.Children.Add(status);

        var addButton = new Button
        {
            Content = "Добавить",
            IsDefault = true,
            MinWidth = 120,
            Style = Res<Style>("PrimaryButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var addCloseButton = new Button
        {
            Content = "Добавить и закрыть",
            MinWidth = 150,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var closeButton = new Button { Content = "Закрыть", IsCancel = true, MinWidth = 90 };

        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { addButton, addCloseButton, closeButton }
        });

        Window window = MakeWindow(owner, "Добавить компьютеры", root, width: 540);
        int added = 0;

        void SetStatus(string text, bool isError)
        {
            status.Text = text;
            status.Foreground = new SolidColorBrush(isError
                ? Color.FromRgb(0xEF, 0x44, 0x44)
                : Color.FromRgb(0x22, 0xC5, 0x5E));
        }

        // true — компьютер добавлен.
        bool AddCurrent()
        {
            string address = host.Text.Trim();

            if (address.Length == 0)
            {
                SetStatus("Введите IP-адрес или DNS-имя.", isError: true);
                host.Focus();
                return false;
            }

            if (group.SelectedItem is not ComputerGroup selectedGroup)
            {
                SetStatus("Выберите группу.", isError: true);
                return false;
            }

            string? error = tryAdd(new SavedComputer
            {
                Name = address,
                Host = address,
                GroupId = selectedGroup.Id,
                Description = description.Text.Trim()
            });

            if (error != null)
            {
                SetStatus(error, isError: true);
                host.SelectAll();
                host.Focus();
                return false;
            }

            added++;
            SetStatus($"Добавлено: {address}  (всего за сеанс: {added})", isError: false);

            // Оставляем IP в поле, чтобы можно было быстро его изменить.
            host.SelectAll();
            host.Focus();

            // Описание очищаем, чтобы случайно не присвоить его следующему ПК.
            description.Clear();

            return true;
        }

        addButton.Click += (_, _) => AddCurrent();

        addCloseButton.Click += (_, _) =>
        {
            // Пустое поле после уже добавленных компьютеров — просто закрываем окно.
            if (host.Text.Trim().Length == 0 && added > 0)
                window.Close();
            else if (AddCurrent())
                window.Close();
        };

        window.Loaded += (_, _) => host.Focus();
        window.ShowDialog();
    }

    public static ComputerGroup? PickGroup(
        Window owner, IReadOnlyList<ComputerGroup> groups, string title)
    {
        ComboBox group = GroupCombo(groups, null);

        bool ok = Show(owner, title, "Добавить",
            new[] { ("Группа", (Control)group) },
            () => group.SelectedItem == null ? "Выберите группу." : null);

        return ok ? group.SelectedItem as ComputerGroup : null;
    }

    private static ComboBox GroupCombo(IReadOnlyList<ComputerGroup> groups, Guid? selectedId) =>
        new()
        {
            ItemsSource = groups,
            DisplayMemberPath = "Name",
            SelectedItem = groups.FirstOrDefault(g => g.Id == selectedId) ?? groups.FirstOrDefault()
        };

    private static void AddField(Panel root, string label, Control input)
    {
        root.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Res<Brush>("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        });
        input.Margin = new Thickness(0, 0, 0, 16);
        root.Children.Add(input);
    }

    private static Window MakeWindow(Window owner, string title, UIElement content, double width = 460) =>
        new()
        {
            Title = title,
            Owner = owner,
            Content = content,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = Res<Style>("AppWindow")
        };

    // validate возвращает текст ошибки или null, если всё в порядке.
    private static bool Show(
        Window owner,
        string title,
        string okText,
        (string Label, Control Input)[] fields,
        Func<string?> validate,
        Control? focus = null)
    {
        var root = new StackPanel { Margin = new Thickness(24) };

        foreach (var (label, input) in fields)
            AddField(root, label, input);

        var ok = new Button
        {
            Content = okText,
            IsDefault = true,
            MinWidth = 120,
            Style = Res<Style>("PrimaryButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 110 };

        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel }
        });

        Window window = MakeWindow(owner, title, root);

        ok.Click += (_, _) =>
        {
            string? error = validate();
            if (error != null)
                MessageBox.Show(window, error, "PingPilot — Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                window.DialogResult = true;
        };

        window.Loaded += (_, _) =>
        {
            Control target = focus ?? fields[0].Input;
            target.Focus();
            if (target is TextBox textBox)
                textBox.SelectAll();
        };

        return window.ShowDialog() == true;
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}

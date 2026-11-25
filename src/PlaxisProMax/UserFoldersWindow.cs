using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PlaxisProMax.Bugs;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// The ⋯ menu's Open folders: each folder Plaxis ProMax keeps things in (your files, bugs, kept code, chats...), with Open
/// and Copy path. Nothing is sent anywhere.
/// </summary>
internal sealed class UserFoldersWindow : Window
{
    private readonly TextBlock _result = Brand.Text("", Brand.K.Muted, 12);
    private static UserFoldersWindow? _open;

    public static void Open(Window? owner)
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            return;
        }
        _open = new UserFoldersWindow { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    internal UserFoldersWindow()
    {
        Title = "Plaxis ProMax: Folders";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);

        var page = new StackPanel { Margin = new Thickness(18, 8, 18, 14) };
        foreach (var folder in UserFolders.List()) page.Children.Add(Row(folder));

        _result.TextWrapping = TextWrapping.Wrap;
        _result.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);
        page.Children.Add(_result);

        var close = Brand.Button("Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        page.Children.Add(Brand.ButtonRow(close));

        var root = new DockPanel();
        var header = Brand.Header("Folders", "Where Plaxis ProMax keeps your files, bugs, kept code and chats");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(page);
        Content = root;
    }

    private UIElement Row(UserFolders.Folder folder)
    {
        var open = Brand.Button("Open");
        AutomationProperties.SetName(open, "Open " + folder.Title);
        open.Click += (_, _) => OpenFolder(folder);
        var copy = Brand.Button("Copy path");
        AutomationProperties.SetName(copy, "Copy the path of " + folder.Title);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(folder.Path); Say($"Copied: {folder.Path}", Brand.K.SuccessText); }
            catch (System.Runtime.InteropServices.ExternalException) { Say("The clipboard is busy. Try Copy path again.", Brand.K.DangerText); }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        copy.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(copy);
        buttons.Children.Add(open);

        var text = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        text.Children.Add(Brand.Text(folder.Title, Brand.K.Text, 13, bold: true));
        var tip = Brand.Text(folder.Tip, Brand.K.Muted, 11);
        tip.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(tip);
        var path = Brand.Text(folder.Path, Brand.K.Muted, 11);
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        path.ToolTip = folder.Path;
        text.Children.Add(path);

        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    private void OpenFolder(UserFolders.Folder folder)
    {
        try
        {
            Directory.CreateDirectory(folder.Path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder.Path}\"") { UseShellExecute = true })?.Dispose();
            Say("", Brand.K.Muted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say($"Couldn't open {folder.Path}: {ex.Message}", Brand.K.DangerText);
        }
    }

    private void Say(string text, string key)
    {
        _result.Text = text;
        Brand.Fg(_result, key);
    }
}

using System.Windows;
using System.Windows.Controls;
using PlaxisProMax.Chat;
using PlaxisProMax.Licensing;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>Settings: when to ask, the standing rules, the Plaxis connection, Plaxis's Python, the model, Claude's sign-in, and the license.</summary>
internal sealed class SettingsWindow : Window
{
    public SettingsWindow(ClaudeCli.Install? claude)
    {
        Title = "Plaxis ProMax settings";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.UseTheme(this);
        if (Brand.Icon is { } icon) Icon = icon;
        SetResourceReference(BackgroundProperty, Brand.K.Bg);

        var s = AgentSettings.Load();
        var level = new ComboBox { ItemsSource = ApprovalPolicy.Levels.Select(ApprovalPolicy.Label).ToList(), SelectedIndex = Array.IndexOf(ApprovalPolicy.Levels, s.Approvals) };
        var hint = ChatUi.Small(ApprovalPolicy.Hint(s.Approvals));
        level.SelectionChanged += (_, _) => hint.Text = ApprovalPolicy.Hint(ApprovalPolicy.Levels[Math.Max(0, level.SelectedIndex)]);
        var silenced = s.NoAskTools ?? [];
        var askAgain = Brand.Button("Ask again");
        askAgain.IsEnabled = silenced.Count > 0;
        var silencedText = ChatUi.Small(silenced.Count == 0 ? "Nothing is set to not ask." : "Not asked about: " + string.Join(", ", silenced.Select(ApprovalPolicy.Friendly)));
        askAgain.Click += (_, _) => { silenced = []; silencedText.Text = "Nothing is set to not ask."; askAgain.IsEnabled = false; };

        var rules = new TextBox { Text = s.ProjectRules, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var port = new TextBox { Text = s.InputPort.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 90 };
        var password = new PasswordBox { Password = s.ServerPassword, Width = 200 };
        var python = new TextBox { Text = s.PlaxisPython };
        var model = new ComboBox { IsEditable = true, ItemsSource = new[] { "", "sonnet", "opus", "haiku", "fable" }, Text = s.ChatModel };
        var timeout = new TextBox { Text = s.CallTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 90 };

        var signIn = Brand.Button("Sign in to Claude again");
        signIn.IsEnabled = claude is not null;
        signIn.Click += (_, _) => { if (claude is not null) ClaudeCli.StartSignIn(claude); };

        var license = Brand.Button("License");
        license.Click += (_, _) => LicenseWindow.Command();
        var save = Brand.Button("Save", Brand.Weight.Primary);
        var cancel = Brand.Button("Cancel");
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) =>
        {
            var fresh = AgentSettings.Load();
            fresh.Approvals = ApprovalPolicy.Levels[Math.Max(0, level.SelectedIndex)];
            fresh.NoAskTools = silenced.ToList();
            fresh.ProjectRules = rules.Text.Trim();
            if (int.TryParse(port.Text, out var p) && p is > 0 and < 65536) fresh.InputPort = p;
            fresh.ServerPassword = password.Password;
            fresh.PlaxisPython = python.Text.Trim();
            fresh.ChatModel = model.Text.Trim();
            if (int.TryParse(timeout.Text, out var t) && t is >= 10 and <= 3600) fresh.CallTimeoutSeconds = t;
            fresh.Save();
            Close();
        };

        var body = new StackPanel { Margin = new Thickness(ChatUi.S4) };
        void Section(string title) => body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1) });
        void Field(string label, FrameworkElement field, string? help = null)
        {
            body.Children.Add(ChatUi.Small(label));
            field.Margin = new Thickness(0, 2, 0, help is null ? ChatUi.S2 : 2);
            field.HorizontalAlignment = field.Width > 0 ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            body.Children.Add(field);
            if (help is not null) { var h = ChatUi.Small(help); h.TextWrapping = TextWrapping.Wrap; h.Margin = new Thickness(0, 0, 0, ChatUi.S2); body.Children.Add(h); }
        }
        Section("When to ask");
        Field("Ask before changes", level);
        body.Children.Add(hint);
        body.Children.Add(silencedText);
        body.Children.Add(Brand.ButtonRow(askAgain));
        Section("Your standing rules");
        Field("Every chat follows these (one per line). The chat adds the ones you tell it.", rules);
        Section("Plaxis");
        Field("Remote scripting port (Plaxis Input)", port);
        Field("Remote scripting password", password, "Only needed when Plaxis ProMax is started outside Plaxis; from Plaxis's Expert menu it is passed on.");
        Field("Plaxis's Python", python, "Empty: found by itself (Bentley's PLAXIS Python Distribution).");
        Field("Time limit per call, seconds (a calculation has its own)", timeout);
        Section("Claude");
        Field("Model (empty: Claude Code's default)", model);
        body.Children.Add(Brand.ButtonRow(signIn));
        Section("License");
        body.Children.Add(ChatUi.Small(LicenseText()));
        body.Children.Add(Brand.ButtonRow(license));
        var buttons = Brand.ButtonRow(save, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, ChatUi.S4, 0, 0);
        body.Children.Add(buttons);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80 };
    }

    private static string LicenseText()
    {
        var s = LicenseState.Current;
        return s.Kind switch
        {
            LicenseState.Kind.Owner => "This is the owner's PC: no key is needed. Keys for other PCs come from Sketch ProMax's License manager, with Plaxis ProMax ticked.",
            LicenseState.Kind.Licensed => $"Licensed to {s.License!.Name}" + (s.Ends is { } e ? $" until {e:d MMM yyyy}." : ", with no end date."),
            _ => "This PC has no valid key yet.",
        };
    }
}

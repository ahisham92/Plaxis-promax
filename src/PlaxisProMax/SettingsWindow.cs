using System.Windows;
using System.Windows.Controls;
using PlaxisProMax.Chat;
using PlaxisProMax.Licensing;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;
using PlaxisProMax.Update;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>Settings: when to ask, the standing rules, the Plaxis connection, Plaxis's Python, the model, Claude's sign-in, the license, updates and bug reports.</summary>
internal sealed class SettingsWindow : Window
{
    public SettingsWindow(ClaudeCli.Install? claude, ChatWindow? chat = null)
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
        var hint = ChatWindow.Wrapped(ChatUi.Small(ApprovalPolicy.Hint(s.Approvals)));
        level.SelectionChanged += (_, _) => hint.Text = ApprovalPolicy.Hint(ApprovalPolicy.Levels[Math.Max(0, level.SelectedIndex)]);
        var silenced = s.NoAskTools ?? [];
        var askAgain = Brand.Button("Ask again");
        askAgain.IsEnabled = silenced.Count > 0;
        var silencedText = ChatWindow.Wrapped(ChatUi.Small(silenced.Count == 0 ? "Nothing is set to not ask." : "Not asked about: " + string.Join(", ", silenced.Select(ApprovalPolicy.Friendly))));
        askAgain.Click += (_, _) => { silenced = []; silencedText.Text = "Nothing is set to not ask."; askAgain.IsEnabled = false; };

        var rules = new TextBox { Text = s.ProjectRules, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var port = new TextBox { Text = s.InputPort.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 90 };
        var password = new PasswordBox { Password = s.ServerPassword, Width = 200, MinHeight = 30, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
        var python = new TextBox { Text = s.PlaxisPython };
        var model = new ComboBox { IsEditable = true, ItemsSource = new[] { "", "sonnet", "opus", "haiku", "fable" }, Text = s.ChatModel };
        var timeout = new TextBox { Text = s.CallTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 90 };

        // The crew (Sketch ProMax's Settings > Chat): the kind of a new chat, how many jobs run at once, the check of finished
        // work, each job's share of the plan, the crew's names, and Automatic's model and effort for each kind of work.
        var chatType = new ComboBox { ItemsSource = ChatTypes.All.Select(ChatTypes.Label).ToList(), SelectedIndex = Math.Max(0, Array.IndexOf(ChatTypes.All, ChatTypes.Parse(s.ChatTypeDefault))), Width = 220 };
        var typeHint = ChatWindow.Wrapped(ChatUi.Small(ChatTypes.Hint(ChatTypes.Parse(s.ChatTypeDefault))));
        chatType.SelectionChanged += (_, _) => typeHint.Text = ChatTypes.Hint(ChatTypes.All[Math.Max(0, chatType.SelectedIndex)]);
        var jobLimit = new ComboBox { ItemsSource = Enumerable.Range(JobBoard.MinLimit, JobBoard.MaxLimit - JobBoard.MinLimit + 1).ToList(), SelectedItem = JobBoard.ClampLimit(s.ChatJobLimit), Width = 90 };
        var review = new CheckBox { Content = "A teammate checks each finished job in the project before it is reported", IsChecked = JobReview.Normal(s.ChatJobReview) == JobReview.Always };
        var budget = new TextBox { Text = s.ChatJobBudget.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 90 };
        var names = CrewNames.From(s.ChatCrewYou, s.ChatCrewCoordinator, s.ChatCrewWorkers, s.ChatCrewApplier, Environment.UserName);
        var crewYou = new TextBox { Text = names.You, Width = 220 };
        var crewCoordinator = new TextBox { Text = names.Coordinator, Width = 220 };
        var crewWorkers = new TextBox { Text = string.Join(", ", names.Workers) };
        var crewApplier = new TextBox { Text = names.Applier, Width = 220 };
        var modelIds = ChatModels.All.Select(m => m.Id).ToList();
        var kindModels = new Dictionary<string, ComboBox>();
        var kindEfforts = new Dictionary<string, ComboBox>();
        FrameworkElement KindRow(string kind, string modelNow, string effortNow)
        {
            var m = new ComboBox { ItemsSource = ChatModels.All.Select(c => c.Label).ToList(), SelectedIndex = Math.Max(0, modelIds.IndexOf(ChatModels.Find(modelNow).Id)), Width = 110 };
            var known = ChatAuto.ParseEffort(effortNow);
            var e = new ComboBox { ItemsSource = ChatAuto.Efforts.Select(ChatAuto.EffortLabel).ToList(), SelectedIndex = Math.Max(0, Array.IndexOf(ChatAuto.Efforts, ChatAuto.IsAuto(known) ? ChatAuto.DefaultEffort(kind) : known)), Width = 110, Margin = new Thickness(ChatUi.S2, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(m, ChatAuto.KindLabel(kind) + ": model");
            System.Windows.Automation.AutomationProperties.SetName(e, ChatAuto.KindLabel(kind) + ": effort");
            kindModels[kind] = m;
            kindEfforts[kind] = e;
            var label = ChatUi.Small(ChatAuto.KindLabel(kind), Brand.K.Text);
            label.Width = 110;
            label.VerticalAlignment = VerticalAlignment.Center;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), ToolTip = ChatAuto.KindHint(kind) };
            row.Children.Add(label);
            row.Children.Add(m);
            row.Children.Add(e);
            return row;
        }
        var kindRows = new StackPanel();
        kindRows.Children.Add(KindRow(ChatAuto.Simple, s.ChatModelSimple, s.ChatEffortSimple));
        kindRows.Children.Add(KindRow(ChatAuto.Everyday, s.ChatModelEveryday, s.ChatEffortEveryday));
        kindRows.Children.Add(KindRow(ChatAuto.Hard, s.ChatModelHard, s.ChatEffortHard));
        kindRows.Children.Add(KindRow(ChatAuto.Coordinating, s.ChatModelCoordinator, s.ChatEffortCoordinator));

        var signIn = Brand.Button("Sign in to Claude again");
        signIn.IsEnabled = claude is not null;
        signIn.Click += (_, _) => { if (claude is not null) ClaudeCli.StartSignIn(claude); };

        // Updates (Sketch ProMax's Update button): what is installed and found, Check for updates, Restart to update.
        var autoUpdate = new CheckBox { Content = "Download updates by themselves", IsChecked = s.AutoUpdate };
        var updateText = ChatWindow.Wrapped(ChatUi.Small(Updater.Describe()));
        var check = Brand.Button("Check for updates");
        var restart = Brand.Button("Restart to update", Brand.Weight.Primary);
        restart.Visibility = Updater.Staged is null ? Visibility.Collapsed : Visibility.Visible;
        check.IsEnabled = chat is not null;
        check.Click += async (_, _) =>
        {
            if (chat is null) return;
            Brand.SetBusy(check, true, "Checking…");
            var progress = new UpdateProgress { Interactive = true };
            progress.Changed += snap => Dispatcher.BeginInvoke(() =>
                updateText.Text = snap.Stage == UpdateStage.Downloading && snap.Percent is { } pct ? $"Downloading… {pct}%" : snap.Detail ?? updateText.Text);
            try { updateText.Text = await chat.CheckForUpdatesAsync(force: true, progress); }
            finally { Brand.SetBusy(check, false); }
            restart.Visibility = Updater.Staged is null ? Visibility.Collapsed : Visibility.Visible;
        };
        restart.Click += (_, _) => { Close(); chat?.RestartToUpdate(); };

        // Bugs (Sketch ProMax's Report bug, Open bug folder and Export chats & bugs).
        var report = Brand.Button("Report bug");
        report.IsEnabled = chat is not null;
        report.Click += (_, _) => chat?.ReportBug(this);
        var bugFolder = Brand.Button("Open bug folder");
        bugFolder.Click += (_, _) => ChatWindow.OpenBugFolder();
        var export = Brand.Button("Export chats & bugs");
        var exportText = ChatWindow.Wrapped(ChatUi.Small("One zip with the last week's chats, every bug report and the tool calls, to send to the developer. Nothing is sent by itself."));
        export.Click += async (_, _) =>
        {
            Brand.SetBusy(export, true, "Exporting…");
            try { exportText.Text = "Saved: " + await ChatWindow.ExportChatsAndBugsAsync(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { exportText.Text = "The export could not be saved: " + ex.Message; }
            finally { Brand.SetBusy(export, false); }
        };

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
            fresh.AutoUpdate = autoUpdate.IsChecked == true;
            fresh.ChatTypeDefault = ChatTypes.All[Math.Max(0, chatType.SelectedIndex)];
            if (jobLimit.SelectedItem is int limit) fresh.ChatJobLimit = limit;
            // "Only in an Autopilot run" (set elsewhere) is kept unless the box is ticked.
            fresh.ChatJobReview = review.IsChecked == true ? JobReview.Always : JobReview.Normal(fresh.ChatJobReview) == JobReview.AutopilotOnly ? JobReview.AutopilotOnly : JobReview.Off;
            if (int.TryParse(budget.Text, out var share) && share is >= 0 and <= 100) fresh.ChatJobBudget = share;
            fresh.ChatCrewYou = crewYou.Text.Trim();
            fresh.ChatCrewCoordinator = crewCoordinator.Text.Trim();
            fresh.ChatCrewWorkers = crewWorkers.Text.Trim();
            fresh.ChatCrewApplier = crewApplier.Text.Trim();
            string ModelOf(string kind) => modelIds[Math.Max(0, kindModels[kind].SelectedIndex)];
            string EffortOf(string kind) => ChatAuto.Efforts[Math.Max(0, kindEfforts[kind].SelectedIndex)];
            (fresh.ChatModelSimple, fresh.ChatEffortSimple) = (ModelOf(ChatAuto.Simple), EffortOf(ChatAuto.Simple));
            (fresh.ChatModelEveryday, fresh.ChatEffortEveryday) = (ModelOf(ChatAuto.Everyday), EffortOf(ChatAuto.Everyday));
            (fresh.ChatModelHard, fresh.ChatEffortHard) = (ModelOf(ChatAuto.Hard), EffortOf(ChatAuto.Hard));
            (fresh.ChatModelCoordinator, fresh.ChatEffortCoordinator) = (ModelOf(ChatAuto.Coordinating), EffortOf(ChatAuto.Coordinating));
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
        Section("Crew");
        Field("A new chat is", chatType);
        typeHint.Margin = new Thickness(0, 0, 0, ChatUi.S2);
        body.Children.Add(typeHint);
        Field("Jobs at once (workers)", jobLimit, "The jobs think side by side; their calls to Plaxis take turns, one at a time.");
        review.Margin = new Thickness(0, 0, 0, ChatUi.S2);
        body.Children.Add(review);
        Field("Each job's share of your 5-hour limit, percent (0: no limit)", budget, "A job that used its share pauses; send it a message to let it carry on.");
        Field("Your name in the crew", crewYou);
        Field("The coordinator", crewCoordinator);
        Field("The workers (one name each, with commas)", crewWorkers);
        Field("Who applies the changes in Plaxis", crewApplier);
        body.Children.Add(ChatUi.Small("Automatic picks a model and an effort for each kind of work:"));
        kindRows.Margin = new Thickness(0, 2, 0, ChatUi.S2);
        body.Children.Add(kindRows);
        Section("Claude");
        Field("One model for every chat (empty: Automatic, as above)", model);
        body.Children.Add(Brand.ButtonRow(signIn));
        Section("License");
        body.Children.Add(ChatWindow.Wrapped(ChatUi.Small(LicenseText())));
        body.Children.Add(Brand.ButtonRow(license));
        Section("Updates");
        body.Children.Add(updateText);
        autoUpdate.Margin = new Thickness(0, ChatUi.S1, 0, ChatUi.S1);
        body.Children.Add(autoUpdate);
        body.Children.Add(Brand.ButtonRow(check, restart));
        Section("Bugs");
        body.Children.Add(exportText);
        body.Children.Add(Brand.ButtonRow(report, bugFolder, export));
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

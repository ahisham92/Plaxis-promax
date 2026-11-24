using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using PlaxisProMax.Phone;

namespace PlaxisProMax.UI;

/// <summary>
/// The chat's Phone switch › Connect your phone: a QR code, shown as the window opens, that the phone scans in Iris to
/// connect Plaxis ProMax to the user's own Iris account (no address to type; PhoneLink.KnownSite). The code carries the message key after the '#', so the
/// website never sees it. Polls every 2.5 s until the phone accepts or the code runs out (10 minutes).
/// </summary>
internal sealed class PhoneConnectWindow : Window
{
    private readonly Image _qr = new() { Width = 240, Height = 240, Margin = new Thickness(0, 12, 0, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted, 12);
    private readonly Button _start = Brand.Button("Show the code", Brand.Weight.Primary);
    private readonly Button _copy = Brand.Button("Copy the link");
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private string _link = "";
    private bool _checking;

    /// <summary>Raised on the UI thread when the phone accepted.</summary>
    public event Action? Connected;

    public PhoneConnectWindow(bool sample = false)
    {
        Title = "Plaxis ProMax: Connect your phone";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);

        var page = new StackPanel { Margin = new Thickness(18, 8, 18, 14) };

        _qr.HorizontalAlignment = HorizontalAlignment.Center;
        RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(_qr, "QR code to scan with your phone");
        page.Children.Add(_qr);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _status.Text = "Getting the code…";
        page.Children.Add(_status);

        _copy.Visibility = Visibility.Collapsed;
        _copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_link); Say("Copied. Open it on your phone (email or message it to yourself).", Brand.K.SuccessText); }
            catch (System.Runtime.InteropServices.ExternalException) { Say("The clipboard is busy. Try again.", Brand.K.DangerText); }
        };
        _start.Click += async (_, _) => await StartAsync();
        var close = Brand.Button("Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        page.Children.Add(Brand.ButtonRow(close, _copy, _start));

        var root = new DockPanel();
        var header = Brand.Header("Connect your phone", "Approvals and replies, with one-tap answers");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(page);
        Content = root;

        _poll.Tick += async (_, _) => await CheckAsync();
        // No address to type: the code is asked for as the window opens.
        if (sample) Loaded += (_, _) => ShowSample();
        else Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _poll.Stop();
    }

    private async Task StartAsync()
    {
        var address = PhoneLink.KnownSite;
        if (address.Length == 0)
        {
            Say("This version doesn't know Iris's address yet; it comes with the next update.", Brand.K.DangerText);
            _start.Visibility = Visibility.Collapsed;
            return;
        }
        _start.IsEnabled = false;
        Say("Getting the code…", Brand.K.Muted);
        try
        {
            _link = await Task.Run(() => PhoneLink.StartAsync(address));
            _qr.Source = Qr(_link);
            _qr.Visibility = Visibility.Visible;
            _copy.Visibility = Visibility.Visible;
            Say("On your phone, open Iris and sign in (or sign up). Then tap Add a connection, scan this code and tap Connect. The code works for 10 minutes.", Brand.K.Muted);
            _poll.Start();
        }
        catch (Exception e)
        {
            Say($"Iris didn't give a code ({e.GetBaseException().Message}). Check the internet connection, then press New code.", Brand.K.DangerText);
        }
        finally { _start.IsEnabled = true; _start.Content = "New code"; }
    }

    /// <summary>For the screenshots: a code as the user sees it, without asking Iris.</summary>
    private void ShowSample()
    {
        _qr.Source = Qr("https://iris.example/iris/pair?c=SAMPLE#k=sample");
        _qr.Visibility = Visibility.Visible;
        _start.Content = "New code";
        Say("On your phone, open Iris and sign in (or sign up). Then tap Add a connection, scan this code and tap Connect. The code works for 10 minutes.", Brand.K.Muted);
    }

    private async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var status = await Task.Run(() => PhoneLink.CheckAsync());
            if (status == "connected")
            {
                _poll.Stop();
                _qr.Visibility = Visibility.Collapsed;
                _copy.Visibility = Visibility.Collapsed;
                _start.Visibility = Visibility.Collapsed;
                Say("Connected. Approvals and replies now go to your phone, and you can write to the chat from it.", Brand.K.SuccessText);
                var error = await Task.Run(() => PhoneLink.SendTestAsync());
                if (error is not null) Say($"Connected, but the test message didn't go ({error}).", Brand.K.DangerText);
                PhoneLink.SetOn(true); // just connected: on, so the test message and what follows reach it
                Connected?.Invoke();
            }
            else if (status == "expired")
            {
                _poll.Stop();
                _qr.Visibility = Visibility.Collapsed;
                Say("The code ran out. Press New code.", Brand.K.DangerText);
            }
        }
        catch (Exception) { /* the next tick tries again */ }
        finally { _checking = false; }
    }

    private void Say(string text, string key)
    {
        _status.Text = text;
        Brand.Fg(_status, key);
    }

    /// <summary>The link as a QR code picture (black on white, whatever the theme: phones read it best that way).</summary>
    private static BitmapImage Qr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}

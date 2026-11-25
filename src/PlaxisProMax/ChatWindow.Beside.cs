using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PlaxisProMax;

/// <summary>
/// The chat as a slim window beside PLAXIS Input or Output (the Plaxis-native look): it stands to the right of the Plaxis
/// window, as tall as it, or at the right of that screen when there is no room. Only this window moves; Plaxis is never
/// moved or resized. It snaps once when Plaxis is first found and no place was saved, and from the ⋯ menu any time.
/// Also the bridge to the Modeller's and Viewer's ProMax panel (Modeller.ChatBridge).
/// </summary>
internal sealed partial class ChatWindow
{
    private const double SlimWidth = 440;
    private bool _snapped;

    /// <summary>Puts the chat beside the Plaxis window. False when no Plaxis Input or Output window is open.</summary>
    private bool SnapBesidePlaxis()
    {
        var r = PlaxisWindowRect();
        if (r is not { } px) return false;
        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var tl = m.Transform(new Point(px.Left, px.Top));
        var br = m.Transform(new Point(px.Right, px.Bottom));
        var plaxis = new Rect(tl, br);
        var area = SystemParameters.WorkArea;
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        Width = SlimWidth;
        var right = plaxis.Right + Width <= area.Right + 1;
        Left = right ? plaxis.Right : area.Right - Width;
        Top = Math.Max(area.Top, plaxis.Top);
        Height = Math.Max(MinHeight, Math.Min(plaxis.Height, area.Bottom - Top));
        _snapped = true;
        return true;
    }

    /// <summary>Snaps once, the first time Plaxis is found, when the window has no saved place of its own.</summary>
    private void SnapFirstTime()
    {
        if (_snapped || _sample || HasSavedPlace) return;
        SnapBesidePlaxis();
    }

    private static bool HasSavedPlace => System.IO.File.Exists(PlaceFile);

    private static RECT? PlaxisWindowRect()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    string name;
                    try { name = p.ProcessName; } catch (InvalidOperationException) { continue; }
                    if (!name.StartsWith("Plaxis", StringComparison.OrdinalIgnoreCase) || name.StartsWith("PlaxisProMax", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.IndexOf("Input", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("Output", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var h = p.MainWindowHandle;
                    if (h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h)) continue;
                    if (GetWindowRect(h, out var rect) && rect.Right - rect.Left > 200) return rect;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    // ---- The Modeller's and Viewer's ProMax panel ------------------------------------------------------------------

    private void AttachBridge()
    {
        Modeller.ChatBridge.Attach(
            text => Dispatcher.Invoke(() =>
            {
                if (_busy) return false;
                Send(text, typed: false);
                return true;
            }),
            () => Dispatcher.BeginInvoke(() =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                _input.Focus();
            }));
    }

    private void Bridge(string kind, string text, string state = "", string tool = "", string? id = null)
    {
        if (_sample) return;
        Modeller.ChatBridge.Add(kind, text, state, tool, id);
    }
}

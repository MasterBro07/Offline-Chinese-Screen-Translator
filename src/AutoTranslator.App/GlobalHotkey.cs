using System;
using System.Windows.Interop;

namespace AutoTranslator.Desktop;

/// <summary>Registers Ctrl+Alt+T system-wide using a message-only window. UI thread only.</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x4154; // arbitrary id, unique within this window

    private readonly Action _onPressed;
    private HwndSource? _source;

    public GlobalHotkey(Action onPressed)
    {
        _onPressed = onPressed;

        var parameters = new HwndSourceParameters("AutoTranslatorHotkeyWindow");
        parameters.Width = 0;
        parameters.Height = 0;
        parameters.WindowStyle = 0;
        parameters.ParentWindow = NativeMethods.HWND_MESSAGE; // message-only window

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        IsRegistered = NativeMethods.RegisterHotKey(
            _source.Handle,
            HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_T);
    }

    /// <summary>False when another program already owns Ctrl+Alt+T.</summary>
    public bool IsRegistered { get; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt64() == HotkeyId)
        {
            handled = true;
            try
            {
                _onPressed();
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey handler failed", ex);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        HwndSource? source = _source;
        _source = null;
        if (source == null)
        {
            return;
        }

        try
        {
            if (IsRegistered)
            {
                NativeMethods.UnregisterHotKey(source.Handle, HotkeyId);
            }

            source.RemoveHook(WndProc);
            source.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey cleanup failed", ex);
        }
    }
}

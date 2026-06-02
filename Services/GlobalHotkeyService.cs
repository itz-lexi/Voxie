using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Voxie.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x564F;
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;
    private Key? _registeredKey;

    public event EventHandler? Pressed;

    public void Register(Window window, Key key)
    {
        if (_source is not null && _registeredKey == key)
            return;

        Unregister();
        var handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(HandleMessage);
        if (!RegisterHotKey(handle, HotkeyId, 0, (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            Unregister();
            var otherVoxieRunning = Process.GetProcessesByName("Voxie").Any(process => process.Id != Environment.ProcessId);
            throw new InvalidOperationException(otherVoxieRunning
                ? $"Could not register {key}. Another Voxie window is already running and may own that shortcut."
                : $"Could not register {key}. Another app may already use that shortcut.");
        }
        _registeredKey = key;
    }

    public void Unregister()
    {
        if (_source is null)
            return;

        UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(HandleMessage);
        _source = null;
        _registeredKey = null;
    }

    public void Dispose() => Unregister();

    private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

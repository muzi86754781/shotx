using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace ShotX.Windows.Core;

public sealed class HotKeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _currentId = 9000;

    public HotKeyManager()
    {
        var parameters = new HwndSourceParameters("ShotX_Hotkey_Helper")
        {
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(HwndHook);
    }

    public int Register(ModifierKeys modifiers, Key key, Action action)
    {
        int id = Interlocked.Increment(ref _currentId);
        uint mod = MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mod |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mod |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mod |= MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mod |= MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (RegisterHotKey(_source.Handle, id, mod, vk))
        {
            _handlers[id] = action;
            return id;
        }
        return -1;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id))
        {
            UnregisterHotKey(_source.Handle, id);
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_handlers.TryGetValue(id, out var handler))
            {
                handler.Invoke();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys.ToList())
        {
            UnregisterHotKey(_source.Handle, id);
        }
        _handlers.Clear();
        _source.RemoveHook(HwndHook);
        _source.Dispose();
    }
}


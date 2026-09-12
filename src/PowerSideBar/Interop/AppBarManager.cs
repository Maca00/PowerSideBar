using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static PowerSideBar.Interop.NativeMethods;

namespace PowerSideBar.Interop;

/// <summary>
/// Manages AppBar registration, positioning, and lifecycle for the sidebar window.
/// </summary>
internal sealed class AppBarManager
{
    private Window? _window;
    private IntPtr _hWnd;
    private HwndSource? _hwndSource;
    private uint _callbackMessageId;
    private bool _isRegistered;
    private bool _isSettingPosition;
    private int _currentWidthPx;
    private RECT _lastAppBarRect;
    private bool _hasLastAppBarRect;
    private ABE _edge = ABE.RIGHT;

    public bool IsRegistered => _isRegistered;
    public bool IsDockedLeft => _edge == ABE.LEFT;

    /// <summary>
    /// Raised after AppBar repositions the window (e.g. on POSCHANGED notification).
    /// </summary>
    public event Action? PositionChanged;

    /// <summary>
    /// Registers the window as an AppBar docked to the configured edge.
    /// </summary>
    public void Register(Window window, int widthPx, bool dockLeft = false)
    {
        _edge = dockLeft ? ABE.LEFT : ABE.RIGHT;

        // Prevent duplicate registration (would add duplicate WndProc hooks)
        if (_isRegistered)
        {
            SetPosition(widthPx);
            return;
        }

        _window = window;
        _currentWidthPx = widthPx;
        _hasLastAppBarRect = false;

        var helper = new WindowInteropHelper(window);
        _hWnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hWnd);
        _hwndSource?.AddHook(WndProc);

        _callbackMessageId = RegisterWindowMessage("PowerSideBar_AppBarMsg");

        var abd = NewAppBarData();
        abd.uCallbackMessage = _callbackMessageId;
        SHAppBarMessage(ABM.NEW, ref abd);
        _isRegistered = true;

        SetPosition(widthPx);
    }

    /// <summary>
    /// Sets the AppBar position and reserves screen space.
    /// Uses the full monitor bounds (rcMonitor), never rcWork — using the work area
    /// causes a feedback loop that eats the desktop from right to left.
    /// </summary>
    public void SetPosition(int widthPx)
    {
        if (!_isRegistered || _window == null || _isSettingPosition)
            return;

        widthPx = Math.Max(1, widthPx);
        _isSettingPosition = true;
        _currentWidthPx = widthPx;
        var changed = false;

        try
        {
            var monitor = GetPrimaryMonitorBoundsPx();
            RECT proposed;
            if (_edge == ABE.LEFT)
            {
                proposed = new RECT
                {
                    Left = monitor.Left,
                    Top = monitor.Top,
                    Right = monitor.Left + widthPx,
                    Bottom = monitor.Bottom,
                };
            }
            else
            {
                proposed = new RECT
                {
                    Left = monitor.Right - widthPx,
                    Top = monitor.Top,
                    Right = monitor.Right,
                    Bottom = monitor.Bottom,
                };
            }

            var abd = NewAppBarData();
            abd.uEdge = _edge;
            abd.rc = proposed;

            // Let the shell resolve conflicts with other AppBars (taskbar, etc.)
            SHAppBarMessage(ABM.QUERYPOS, ref abd);

            // Keep requested width against the (possibly adjusted) dock edge
            if (_edge == ABE.LEFT)
                abd.rc.Right = abd.rc.Left + widthPx;
            else
                abd.rc.Left = abd.rc.Right - widthPx;

            // Skip no-op updates — POSCHANGED / WINDOWPOSCHANGED would otherwise loop forever
            if (_hasLastAppBarRect &&
                _lastAppBarRect.Left == abd.rc.Left &&
                _lastAppBarRect.Top == abd.rc.Top &&
                _lastAppBarRect.Right == abd.rc.Right &&
                _lastAppBarRect.Bottom == abd.rc.Bottom)
            {
                return;
            }

            // MSDN order: SETPOS first, then move the HWND to match
            SHAppBarMessage(ABM.SETPOS, ref abd);

            SetWindowPos(_hWnd, IntPtr.Zero,
                abd.rc.Left, abd.rc.Top,
                abd.rc.Right - abd.rc.Left, abd.rc.Bottom - abd.rc.Top,
                SWP_NOZORDER | SWP_NOACTIVATE);

            _lastAppBarRect = abd.rc;
            _hasLastAppBarRect = true;
            changed = true;
        }
        finally
        {
            // Keep the guard through PositionChanged so overlay SetWindowPos
            // does not re-enter via WM_WINDOWPOSCHANGED → POSCHANGED.
            try
            {
                if (changed)
                    PositionChanged?.Invoke();
            }
            finally
            {
                _isSettingPosition = false;
            }
        }
    }

    /// <summary>
    /// Unregisters the AppBar and releases screen space.
    /// </summary>
    public void Unregister()
    {
        if (!_isRegistered)
            return;

        var abd = NewAppBarData();
        SHAppBarMessage(ABM.REMOVE, ref abd);
        _isRegistered = false;
        _hasLastAppBarRect = false;

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _window = null;
        _hWnd = IntPtr.Zero;
        _callbackMessageId = 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_callbackMessageId)
        {
            var notification = (ABN)unchecked((uint)(long)wParam);
            switch (notification)
            {
                case ABN.POSCHANGED:
                    // External layout change (taskbar move, other AppBar, display).
                    // No-op guard inside SetPosition breaks self-triggered loops.
                    if (!_isSettingPosition)
                        SetPosition(_currentWidthPx);
                    handled = true;
                    break;

                case ABN.FULLSCREENAPP:
                    if (_window != null)
                    {
                        bool fullScreen = (long)lParam != 0;
                        _window.Topmost = !fullScreen;
                        if (!fullScreen && !_isSettingPosition)
                            SetPosition(_currentWidthPx);
                    }
                    handled = true;
                    break;
            }
        }
        else if (msg == WM_ACTIVATE)
        {
            if (_isRegistered)
            {
                var abd = NewAppBarData();
                SHAppBarMessage(ABM.ACTIVATE, ref abd);
            }
        }
        else if (msg == WM_WINDOWPOSCHANGED)
        {
            // Do not notify the shell while we are applying our own SETPOS/Move —
            // that feedback is what freezes Explorer when combined with rcWork bugs.
            if (_isRegistered && !_isSettingPosition)
            {
                var abd = NewAppBarData();
                SHAppBarMessage(ABM.WINDOWPOSCHANGED, ref abd);
            }
        }

        return IntPtr.Zero;
    }

    private APPBARDATA NewAppBarData()
    {
        return new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hWnd,
        };
    }

    private double GetDpiScale()
    {
        if (_hwndSource?.CompositionTarget != null)
        {
            return _hwndSource.CompositionTarget.TransformToDevice.M11;
        }
        return 1.0;
    }

    public void RefreshPosition()
    {
        SetPosition(_currentWidthPx);
    }

    /// <summary>Changes dock edge and repositions if already registered.</summary>
    public void SetDockLeft(bool dockLeft)
    {
        var edge = dockLeft ? ABE.LEFT : ABE.RIGHT;
        if (_edge == edge)
            return;

        _edge = edge;
        _hasLastAppBarRect = false;
        if (_isRegistered)
            SetPosition(_currentWidthPx);
    }

    /// <summary>
    /// Moves the HWND back to the last reserved AppBar rect without calling SETPOS.
    /// Needed after overlay mode: PositionOverlay moves the window away from the
    /// reserved strip, and a no-op SetPosition would otherwise leave a large empty window.
    /// </summary>
    public void SyncWindowToAppBar()
    {
        if (!_isRegistered || !_hasLastAppBarRect || _hWnd == IntPtr.Zero || _isSettingPosition)
            return;

        _isSettingPosition = true;
        try
        {
            SetWindowPos(_hWnd, IntPtr.Zero,
                _lastAppBarRect.Left, _lastAppBarRect.Top,
                _lastAppBarRect.Right - _lastAppBarRect.Left,
                _lastAppBarRect.Bottom - _lastAppBarRect.Top,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        finally
        {
            _isSettingPosition = false;
        }
    }

    /// <summary>
    /// Full primary monitor rectangle in physical pixels (not the work area).
    /// </summary>
    private RECT GetPrimaryMonitorBoundsPx()
    {
        var monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MONITORINFO
            {
                cbSize = Marshal.SizeOf<MONITORINFO>(),
            };

            if (GetMonitorInfo(monitor, ref monitorInfo))
                return monitorInfo.rcMonitor;
        }

        var dpi = GetDpiScale();
        return new RECT
        {
            Left = 0,
            Top = 0,
            Right = (int)(SystemParameters.PrimaryScreenWidth * dpi),
            Bottom = (int)(SystemParameters.PrimaryScreenHeight * dpi),
        };
    }
}

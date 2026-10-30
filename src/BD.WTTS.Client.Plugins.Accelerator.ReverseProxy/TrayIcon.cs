// ReSharper disable once InconsistentNaming
namespace BD.WTTS.Services.Implementation;

/// <summary>
/// Windows 系统托盘图标。
/// <para>
/// 直接用 Win32 的 <c>Shell_NotifyIcon</c> + 消息窗口实现，不引入 WinForms——
/// 引入 WinForms 会把工程从裸 TFM（<c>net11.0</c>）变成 <c>net11.0-windows</c>，
/// 破坏「在非 Windows 主机上交叉编译 win-x64」的能力，也与裁剪目标相悖。
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    const int WM_DESTROY = 0x0002;
    const int WM_USER = 0x0400;
    const int WM_RBUTTONUP = 0x0205;
    const int WM_LBUTTONDBLCLK = 0x0203;

    const int NIM_ADD = 0x00000000;
    const int NIM_DELETE = 0x00000002;
    const int NIM_MODIFY = 0x00000001;

    const int NIF_MESSAGE = 0x00000001;
    const int NIF_ICON = 0x00000002;
    const int NIF_TIP = 0x00000004;

    const int TPM_RIGHTBUTTON = 0x0002;
    const int TPM_RETURNCMD = 0x0100;
    const int MF_STRING = 0x0000;
    const int MF_SEPARATOR = 0x0800;

    const int SW_HIDE = 0;
    const int SW_SHOW = 5;

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_APPWINDOW = 0x00040000;

    const int ID_TRAY = 0x5654;
    const int WM_TRAY = WM_USER + 1;
    const int CMD_SHOW_CONSOLE = 1001;
    const int CMD_EXIT = 1002;

    const string WINDOW_CLASS_BASE = "SteamPPAcceleratorTray";

    /// <summary>
    /// 窗口类名。附带进程 ID 避免多实例互相覆盖类注册。
    /// </summary>
    readonly string windowClass = WINDOW_CLASS_BASE + "_" + Environment.ProcessId;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string? lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProc(IntPtr hWnd, int uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgMin, uint wMsgMax);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    static extern uint GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 取当前进程的控制台窗口句柄。
    /// <para>
    /// 注意导出模块是 <c>kernel32.dll</c> 而不是 <c>user32.dll</c>——
    /// 写成 user32 会在调用时抛 <see cref="EntryPointNotFoundException"/>（不是返回 0，异常直接上抛）。
    /// </para>
    /// </summary>
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int x;
        public int y;
    }

    /// <summary>窗口过程委托必须保持引用，否则会被 GC 回收导致崩溃。</summary>
    readonly WndProcDelegate wndProcDelegate;

    readonly IntPtr moduleHandle;
    IntPtr iconLarge;
    IntPtr iconSmall;

    IntPtr windowHandle;
    IntPtr menu;
    uint messageThreadId;
    string tip;
    bool disposed;
    Thread? messageThread;

    /// <summary>点击「显示控制台」时触发。</summary>
    public Action? ShowConsoleRequested { get; set; }

    /// <summary>点击「退出」时触发。</summary>
    public Action? ExitRequested { get; set; }

    delegate IntPtr WndProcDelegate(IntPtr hWnd, int uMsg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 定位用于提取托盘图标的可执行文件。
    /// <para>
    /// 通过 <c>dotnet xxx.dll</c> 启动时 <see cref="Environment.ProcessPath"/> 指向的是 dotnet 宿主，
    /// 其图标并非本程序图标，需要改用程序集同目录下的同名 exe。
    /// </para>
    /// </summary>
    public static string ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var assemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            var exePath = Path.ChangeExtension(assemblyPath, ".exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }
        }

        return processPath ?? string.Empty;
    }

    public TrayIcon(string tipText, string iconPath)
    {
        tip = tipText;
        wndProcDelegate = WndProc;
        moduleHandle = GetModuleHandle(null);

        // 从 exe 提取图标；失败时退回系统默认应用图标
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        if (ExtractIconEx(iconPath, 0, large, small, 1) > 0)
        {
            iconLarge = large[0];
            iconSmall = small[0];
        }
        if (iconSmall == IntPtr.Zero)
        {
            iconSmall = iconLarge;
        }
        if (iconLarge == IntPtr.Zero)
        {
            iconLarge = LoadIcon(IntPtr.Zero, new IntPtr(32512 /* IDI_APPLICATION */));
            iconSmall = iconLarge;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    /// <summary>
    /// 在后台线程创建托盘图标并进入消息循环。
    /// </summary>
    /// <returns>托盘图标是否创建成功。</returns>
    public bool Start()
    {
        messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "Steam++Accelerator Tray",
        };
        messageThread.Start();
        started.Wait(TimeSpan.FromSeconds(5));
        return started.IsSet && trayCreated;
    }

    readonly ManualResetEventSlim started = new(false);
    volatile bool trayCreated;

    /// <summary>
    /// 更新托盘提示文字。
    /// </summary>
    public void SetTip(string tipText)
    {
        tip = tipText;
        if (trayCreated)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = windowHandle,
                uID = ID_TRAY,
                uFlags = NIF_TIP,
                szTip = tip,
            };
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    /// <summary>
    /// 隐藏控制台窗口（托盘运行时调用）。
    /// <para>
    /// 只有当本进程是该控制台的唯一使用者时才隐藏——否则会把用户自己的终端窗口也藏掉。
    /// </para>
    /// </summary>
    public static bool HideConsoleWindow()
    {
        var hWnd = GetConsoleWindow();
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        if (!OwnsConsole())
        {
            // 控制台被其它进程共用（用户从终端启动的），不隐藏
            return false;
        }

        ShowWindow(hWnd, SW_HIDE);
        return true;
    }

    /// <summary>
    /// 本进程是否是该控制台的唯一使用者。
    /// </summary>
    static bool OwnsConsole()
    {
        var count = GetConsoleProcessList(0, null);
        if (count <= 1)
        {
            return true;
        }

        var pids = new uint[count];
        return GetConsoleProcessList(count, pids) == 1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetConsoleProcessList(uint dwProcessCount, uint[]? lpdwProcessList);

    /// <summary>
    /// 显示并激活控制台窗口。
    /// </summary>
    public static void ShowConsoleWindow()
    {
        var hWnd = GetConsoleWindow();
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        // 控制台窗口默认带 WS_EX_APPWINDOW 之外还需去掉 TOOLWINDOW 才能在任务栏显示
        var exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, exStyle & ~WS_EX_TOOLWINDOW | WS_EX_APPWINDOW);
        ShowWindow(hWnd, SW_SHOW);
        SetForegroundWindow(hWnd);
    }

    void MessageLoop()
    {
        try
        {
            MessageLoopCore();
        }
        catch (Exception ex)
        {
            // 托盘失败不应影响代理主流程
            Log.Error("TrayIcon", $"托盘消息循环异常: {ex}");
        }
        finally
        {
            started.Set();
        }
    }

    void MessageLoopCore()
    {
        messageThreadId = GetCurrentThreadId();

        var wndClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate),
            hInstance = moduleHandle,
            lpszClassName = windowClass,
        };
        var classAtom = RegisterClassEx(ref wndClass);
        if (classAtom == IntPtr.Zero)
        {
            Log.Warn("TrayIcon", $"RegisterClassEx 失败, error={Marshal.GetLastWin32Error()}");
        }

        // 用 WS_EX_TOOLWINDOW 的顶层窗口：不在任务栏、不进 Alt-Tab，
        // 但窗口句柄仍可被 FindWindow/FindWindowEx 按类名定位（HWND_MESSAGE 不行）。
        var exStyle = WS_EX_TOOLWINDOW;
        windowHandle = CreateWindowEx(
            exStyle, windowClass, "Steam++ Accelerator", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, moduleHandle, IntPtr.Zero);
        if (windowHandle == IntPtr.Zero)
        {
            Log.Warn("TrayIcon", $"CreateWindowEx 失败, error={Marshal.GetLastWin32Error()}");
            return;
        }

        menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, CMD_SHOW_CONSOLE, "显示控制台");
        AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, string.Empty);
        AppendMenu(menu, MF_STRING, CMD_EXIT, "退出");

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = windowHandle,
            uID = ID_TRAY,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = iconSmall != IntPtr.Zero ? iconSmall : iconLarge,
            szTip = tip,
        };
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            Log.Warn("TrayIcon", $"Shell_NotifyIcon(NIM_ADD) 失败, error={Marshal.GetLastWin32Error()}, cbSize={data.cbSize}, hIcon={data.hIcon}");
            started.Set();
            return;
        }
        trayCreated = true;
        started.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // 窗口必须在创建它的线程销毁
        if (windowHandle != IntPtr.Zero)
        {
            DestroyWindow(windowHandle);
            windowHandle = IntPtr.Zero;
        }
    }

    IntPtr WndProc(IntPtr hWnd, int uMsg, IntPtr wParam, IntPtr lParam)
    {
        switch (uMsg)
        {
            case WM_TRAY:
                if (lParam.ToInt32() == WM_RBUTTONUP)
                {
                    ShowContextMenu();
                }
                else if (lParam.ToInt32() == WM_LBUTTONDBLCLK)
                {
                    ShowConsoleRequested?.Invoke();
                }
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, uMsg, wParam, lParam);
    }

    void ShowContextMenu()
    {
        GetCursorPos(out var point);
        // 先置前再显示，菜单关闭后恢复，否则点击其它窗口时菜单不会消失
        SetForegroundWindow(windowHandle);
        var cmd = TrackPopupMenu(
            menu,
            TPM_RIGHTBUTTON | TPM_RETURNCMD,
            point.x,
            point.y,
            0,
            windowHandle,
            IntPtr.Zero);
        PostMessage(windowHandle, 0, IntPtr.Zero, IntPtr.Zero);

        if (cmd == CMD_EXIT)
        {
            ExitRequested?.Invoke();
        }
        else if (cmd == CMD_SHOW_CONSOLE)
        {
            ShowConsoleRequested?.Invoke();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        if (trayCreated)
        {
            // 删除图标只需 hWnd + uID
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = windowHandle,
                uID = ID_TRAY,
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            trayCreated = false;
        }

        if (menu != IntPtr.Zero)
        {
            DestroyMenu(menu);
            menu = IntPtr.Zero;
        }

        if (windowHandle != IntPtr.Zero)
        {
            // 用 WM_QUIT 直接结束消息循环，而不是走 WM_DESTROY 分发——
            // 后者会在 WndProc 里执行托管代码，进程退出阶段可能撞上运行时已销毁的断言。
            // 窗口本身由消息线程在循环结束后销毁（DestroyWindow 必须同线程调用）。
            PostThreadMessage(messageThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        // 必须等消息线程真正退出，否则进程结束时会触发
        // "Attempt to execute managed code after the .NET runtime thread state has been destroyed"
        var joined = messageThread?.Join(TimeSpan.FromSeconds(5)) ?? true;
        try
        {
            Log.Info("TrayIcon", $"托盘消息线程已退出: {joined}");
        }
        catch
        {
            // 日志设施可能已释放，此处不应影响退出
        }
    }

    const int WM_QUIT = 0x0012;

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);
}

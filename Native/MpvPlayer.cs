using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace FFmpegCutStudio.Native;

/// <summary>
/// 轻量级 libmpv P/Invoke 封装：
/// - 自动兼容 libmpv-2.dll 与 mpv-2.dll
/// - 强制 Windows 稳定渲染后端 (vo=gpu, gpu-api=d3d11, d3d11-flip=no) 避免黑屏
/// - 区分拖拽快速定位 (keyframes) 与松手精准定位 (exact)，防止拖拽排队快进
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private const string LogicalDllName = "libmpv-2.dll";
    private const int MpvFormatFlag = 3;
    private const int MpvFormatInt64 = 4;
    private const int MpvFormatDouble = 5;
    private const int MpvEventNone = 0;
    private const int MpvEventShutdown = 1;
    private const int MpvEventLogMessage = 2;

    private static bool _resolverRegistered;
    private IntPtr _ctx = IntPtr.Zero;
    private CancellationTokenSource? _eventPumpCts;

    public bool IsInitialized => _ctx != IntPtr.Zero;
    public event Action<string>? LogMessageReceived;

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserData;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEventLogMessageStruct
    {
        public IntPtr Prefix;
        public IntPtr Level;
        public IntPtr Text;
        public int LogLevel;
    }

    static MpvPlayer()
    {
        RegisterDllResolver();
    }

    private static void RegisterDllResolver()
    {
        if (_resolverRegistered) return;
        _resolverRegistered = true;

        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (libraryName, assembly, searchPath) =>
        {
            if (libraryName != LogicalDllName) return IntPtr.Zero;

            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "libmpv-2.dll"),
                Path.Combine(baseDir, "mpv-2.dll"),
                "libmpv-2.dll",
                "mpv-2.dll"
            };

            foreach (var c in candidates)
            {
                if (NativeLibrary.TryLoad(c, out IntPtr handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    public static bool IsLibraryAvailable(out string? foundPath)
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (var name in new[] { "libmpv-2.dll", "mpv-2.dll" })
        {
            string full = Path.Combine(baseDir, name);
            if (File.Exists(full) && NativeLibrary.TryLoad(full, out IntPtr h))
            {
                NativeLibrary.Free(h);
                foundPath = full;
                return true;
            }
            if (NativeLibrary.TryLoad(name, out IntPtr h2))
            {
                NativeLibrary.Free(h2);
                foundPath = name;
                return true;
            }
        }
        foundPath = null;
        return false;
    }

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_create();

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_initialize(IntPtr ctx);

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_request_log_messages(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        ref long data);

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option_string(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        out double data);

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        out int data);

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_property(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        ref int data);

    [DllImport(LogicalDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_property(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        ref double data);

    public void Initialize(IntPtr windowHandle)
    {
        if (_ctx != IntPtr.Zero) return;

        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create() 返回空指针。");

        long wid = windowHandle.ToInt64();
        mpv_set_option(_ctx, "wid", MpvFormatInt64, ref wid);

        mpv_set_option_string(_ctx, "config", "no");
        mpv_set_option_string(_ctx, "vo", "gpu");
        mpv_set_option_string(_ctx, "gpu-api", "d3d11");
        mpv_set_option_string(_ctx, "gpu-context", "d3d11");
        mpv_set_option_string(_ctx, "d3d11-flip", "no");
        mpv_set_option_string(_ctx, "hwdec", "auto-copy");
        mpv_set_option_string(_ctx, "force-window", "yes");
        mpv_set_option_string(_ctx, "keep-open", "yes");
        mpv_set_option_string(_ctx, "osc", "no");
        mpv_set_option_string(_ctx, "window-dragging", "no");
        mpv_set_option_string(_ctx, "input-default-bindings", "no");
        mpv_set_option_string(_ctx, "input-vo-keyboard", "no");
        mpv_set_option_string(_ctx, "hr-seek", "yes");
        mpv_request_log_messages(_ctx, "warn");

        int err = mpv_initialize(_ctx);
        if (err < 0)
        {
            mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
            throw new InvalidOperationException($"mpv_initialize() 失败，错误码: {err}");
        }

        _eventPumpCts = new CancellationTokenSource();
        var token = _eventPumpCts.Token;
        _ = Task.Run(() => EventPumpLoop(token), token);
    }

    private void EventPumpLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _ctx != IntPtr.Zero)
        {
            IntPtr evPtr = mpv_wait_event(_ctx, 0.2);
            if (evPtr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            if (ev.EventId == MpvEventNone) continue;
            if (ev.EventId == MpvEventShutdown) break;

            if (ev.EventId == MpvEventLogMessage && ev.Data != IntPtr.Zero)
            {
                var msg = Marshal.PtrToStructure<MpvEventLogMessageStruct>(ev.Data);
                string prefix = Marshal.PtrToStringUTF8(msg.Prefix) ?? "mpv";
                string text = (Marshal.PtrToStringUTF8(msg.Text) ?? "").Trim();
                if (!string.IsNullOrEmpty(text))
                    LogMessageReceived?.Invoke($"[mpv/{prefix}] {text}");
            }
        }
    }

    public void Command(params string[] args)
    {
        if (_ctx == IntPtr.Zero) return;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(args[i] + "\0");
                IntPtr mem = Marshal.AllocHGlobal(utf8.Length);
                Marshal.Copy(utf8, 0, mem, utf8.Length);
                ptrs[i] = mem;
            }
            ptrs[args.Length] = IntPtr.Zero;
            mpv_command(_ctx, ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (ptrs[i] != IntPtr.Zero)
                    Marshal.FreeHGlobal(ptrs[i]);
            }
        }
    }

    public void LoadFile(string path)
    {
        Command("loadfile", path, "replace");
        SetPaused(false);
    }

    public void AddSubtitle(string subtitlePath) => Command("sub-add", subtitlePath, "select");

    /// <summary>拖拽进度条时使用的极速关键帧预览定位，避免命令积压。</summary>
    public void SeekFast(double seconds) =>
        Command("seek", Math.Max(0, seconds).ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute", "keyframes");

    /// <summary>点击或松开鼠标后的精准帧定位。</summary>
    public void SeekAbsolute(double seconds) =>
        Command("seek", Math.Max(0, seconds).ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute", "exact");

    public void SeekRelative(double deltaSeconds) =>
        Command("seek", deltaSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "relative", "exact");

    public void FrameStep() => Command("frame-step");

    public void FrameBackStep() => Command("frame-back-step");

    public bool IsPaused()
    {
        if (_ctx == IntPtr.Zero) return true;
        return mpv_get_property(_ctx, "pause", MpvFormatFlag, out int val) >= 0 && val != 0;
    }

    public void SetPaused(bool paused)
    {
        if (_ctx == IntPtr.Zero) return;
        int val = paused ? 1 : 0;
        mpv_set_property(_ctx, "pause", MpvFormatFlag, ref val);
    }

    public void TogglePause() => SetPaused(!IsPaused());

    public double GetTimePos()
    {
        if (_ctx == IntPtr.Zero) return 0;
        return mpv_get_property(_ctx, "time-pos", MpvFormatDouble, out double val) >= 0 ? val : 0;
    }

    public double GetDuration()
    {
        if (_ctx == IntPtr.Zero) return 0;
        return mpv_get_property(_ctx, "duration", MpvFormatDouble, out double val) >= 0 ? val : 0;
    }

    public void SetVolume(double volume)
    {
        if (_ctx == IntPtr.Zero) return;
        double v = Math.Clamp(volume, 0, 100);
        mpv_set_property(_ctx, "volume", MpvFormatDouble, ref v);
    }

    public void Dispose()
    {
        _eventPumpCts?.Cancel();
        _eventPumpCts?.Dispose();
        _eventPumpCts = null;

        if (_ctx != IntPtr.Zero)
        {
            mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}

public sealed class MpvHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    private IntPtr _hwnd = IntPtr.Zero;
    public MpvPlayer Player { get; } = new();
    public string? InitError { get; private set; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hwndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(
            0, "static", "",
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0, 0, 640, 360,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        try
        {
            Player.Initialize(_hwnd);
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
        }

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Player.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}

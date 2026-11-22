using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FFmpegCutStudio.Models;
using FFmpegCutStudio.Native;
using FFmpegCutStudio.Services;
using Microsoft.Win32;

namespace FFmpegCutStudio;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private MpvHost? _mpvHost;
    private bool _useMpv;
    private bool _fallbackPaused = true;

    private readonly FFmpegEngine _engine = new();
    private readonly DispatcherTimer _syncTimer;

    private string? _currentVideoPath;
    private MediaProbeInfo? _currentProbe;
    private readonly ObservableCollection<ClipSegment> _segments = new();
    private readonly ObservableCollection<string> _mergeFiles = new();
    private CancellationTokenSource? _taskCts;

    public MainWindow()
    {
        InitializeComponent();

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1100, Math.Max(MinWidth, workArea.Width * 0.90));
        Height = Math.Min(660, Math.Max(MinHeight, workArea.Height * 0.90));

        InitializePreviewEngine();

        ListSegments.ItemsSource = _segments;
        ListMergeFiles.ItemsSource = _mergeFiles;
        Timeline.Segments = _segments;

        Timeline.ScrubRequested += SeekFast;
        Timeline.SeekCommitted += SeekAbsolute;
        Timeline.SegmentSelected += seg =>
        {
            ListSegments.SelectedItem = seg;
            Timeline.SelectedSegment = seg;
        };
        Timeline.SegmentBoundsChanged += _ => ListSegments.Items.Refresh();

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _syncTimer.Tick += SyncTimer_Tick;
        _syncTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 启用 Windows 10/11 原生深色标题栏，消除顶部白条
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch { }
    }

    private void InitializePreviewEngine()
    {
        if (MpvPlayer.IsLibraryAvailable(out string? foundDll))
        {
            _mpvHost = new MpvHost();
            _mpvHost.Player.LogMessageReceived += msg => AppendLog(msg);
            VideoHostContainer.Children.Add(_mpvHost);
            _useMpv = true;
            TxtPreviewBackend.Text = "libmpv (D3D11)";
            AppendLog($"[预览] 已检测到 {foundDll}，启用 libmpv 硬件解码预览。");

            Loaded += (_, _) =>
            {
                if (_mpvHost != null && !string.IsNullOrEmpty(_mpvHost.InitError))
                {
                    AppendLog($"[警告] libmpv 初始化失败 ({_mpvHost.InitError})，自动切换为 WPF 内置预览。");
                    SwitchToFallbackPreview();
                }
            };
        }
        else
        {
            SwitchToFallbackPreview();
            AppendLog($"[提示] 未在程序目录 ({AppContext.BaseDirectory}) 找到 libmpv-2.dll 或 mpv-2.dll，已自动启用 WPF 内置预览。");
        }
    }

    private void SwitchToFallbackPreview()
    {
        _useMpv = false;
        if (_mpvHost != null)
        {
            VideoHostContainer.Children.Remove(_mpvHost);
            _mpvHost.Dispose();
            _mpvHost = null;
        }
        FallbackMediaElement.Visibility = Visibility.Visible;
        TxtPreviewBackend.Text = "WPF 内置预览";
    }

    private void PlayerLoadFile(string path)
    {
        if (_useMpv && _mpvHost != null)
        {
            _mpvHost.Player.LoadFile(path);
        }
        else
        {
            FallbackMediaElement.Stop();
            FallbackMediaElement.Source = new Uri(Path.GetFullPath(path));
            FallbackMediaElement.Play();
            _fallbackPaused = false;
        }
    }

    private void TogglePause()
    {
        if (string.IsNullOrEmpty(_currentVideoPath)) return;
        if (_useMpv && _mpvHost != null)
        {
            _mpvHost.Player.TogglePause();
        }
        else
        {
            if (_fallbackPaused)
            {
                FallbackMediaElement.Play();
                _fallbackPaused = false;
            }
            else
            {
                FallbackMediaElement.Pause();
                _fallbackPaused = true;
            }
        }
    }

    private void SeekFast(double seconds)
    {
        if (string.IsNullOrEmpty(_currentVideoPath)) return;
        double clamped = Math.Clamp(seconds, 0, Math.Max(0, Timeline.DurationSeconds));
        if (_useMpv && _mpvHost != null)
            _mpvHost.Player.SeekFast(clamped);
        else
            FallbackMediaElement.Position = TimeSpan.FromSeconds(clamped);
        TxtTimecode.Text = $"{ClipSegment.FormatTime(clamped)} / {ClipSegment.FormatTime(Timeline.DurationSeconds)}";
    }

    private void SeekAbsolute(double seconds)
    {
        if (string.IsNullOrEmpty(_currentVideoPath)) return;
        double clamped = Math.Clamp(seconds, 0, Math.Max(0, Timeline.DurationSeconds));
        if (_useMpv && _mpvHost != null)
            _mpvHost.Player.SeekAbsolute(clamped);
        else
            FallbackMediaElement.Position = TimeSpan.FromSeconds(clamped);
        TxtTimecode.Text = $"{ClipSegment.FormatTime(clamped)} / {ClipSegment.FormatTime(Timeline.DurationSeconds)}";
    }

    private void SeekRelative(double deltaSeconds)
    {
        if (string.IsNullOrEmpty(_currentVideoPath)) return;
        if (_useMpv && _mpvHost != null)
            _mpvHost.Player.SeekRelative(deltaSeconds);
        else
            SeekAbsolute(GetCurrentPosition() + deltaSeconds);
    }

    private void StepFrame(int direction)
    {
        if (string.IsNullOrEmpty(_currentVideoPath)) return;
        if (_useMpv && _mpvHost != null)
        {
            if (direction > 0) _mpvHost.Player.FrameStep();
            else _mpvHost.Player.FrameBackStep();
        }
        else
        {
            double fps = (_currentProbe?.Fps > 1) ? _currentProbe.Fps : 30.0;
            FallbackMediaElement.Pause();
            _fallbackPaused = true;
            SeekAbsolute(GetCurrentPosition() + direction * (1.0 / fps));
        }
    }

    private double GetCurrentPosition()
    {
        if (_useMpv && _mpvHost != null && _mpvHost.Player.IsInitialized)
            return _mpvHost.Player.GetTimePos();
        return FallbackMediaElement.Position.TotalSeconds;
    }

    private double GetCurrentDuration()
    {
        if (_useMpv && _mpvHost != null && _mpvHost.Player.IsInitialized)
            return _mpvHost.Player.GetDuration();
        return FallbackMediaElement.NaturalDuration.HasTimeSpan
            ? FallbackMediaElement.NaturalDuration.TimeSpan.TotalSeconds
            : 0;
    }

    private void FallbackMediaElement_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        AppendLog($"[预览失败] WPF 内置内核无法解码该格式 ({e.ErrorException?.Message})。请将 64 位 libmpv-2.dll 放到程序目录。");
    }

    private void SyncTimer_Tick(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_currentVideoPath) || Timeline.IsDragging) return;
        double pos = GetCurrentPosition();
        double dur = GetCurrentDuration();
        if (dur > 0 && Math.Abs(Timeline.DurationSeconds - dur) > 0.05)
            Timeline.DurationSeconds = dur;
        Timeline.CurrentSeconds = pos;
        TxtTimecode.Text = $"{ClipSegment.FormatTime(pos)} / {ClipSegment.FormatTime(Timeline.DurationSeconds)}";
    }

    private async void BtnOpenVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择视频文件",
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.flv;*.ts;*.webm;*.m4v|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true)
            await LoadVideoAsync(dlg.FileName);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 0) return;

        string first = files[0];
        string ext = Path.GetExtension(first).ToLowerInvariant();
        if (ext is ".srt" or ".ass" or ".ssa" or ".vtt")
        {
            TxtSoftSubPath.Text = first;
            TxtHardSubPath.Text = first;
            if (_useMpv && _mpvHost != null)
            {
                _mpvHost.Player.AddSubtitle(first);
                AppendLog($"[字幕] 已拖入并加载到 libmpv 预览: {Path.GetFileName(first)}");
            }
            else
            {
                AppendLog($"[字幕] 已填入字幕路径: {Path.GetFileName(first)}");
            }
        }
        else
        {
            await LoadVideoAsync(first);
        }
    }

    private async Task LoadVideoAsync(string path)
    {
        _currentVideoPath = path;
        TxtCurrentFile.Text = Path.GetFileName(path);
        TxtCurrentFile.ToolTip = path;
        TxtMuxVideo.Text = path;
        TxtPlayerHint.Visibility = Visibility.Collapsed;

        SyncEnginePaths();
        PlayerLoadFile(path);

        _segments.Clear();
        Timeline.Keyframes = Array.Empty<double>();
        AppendLog($"[加载] {path}");

        try
        {
            _currentProbe = await _engine.ProbeMediaAsync(path);
            Timeline.DurationSeconds = _currentProbe.DurationSeconds;

            var seg1 = new ClipSegment
            {
                Name = "分段 1",
                StartSeconds = 0,
                EndSeconds = _currentProbe.DurationSeconds
            };
            _segments.Add(seg1);
            ListSegments.SelectedItem = seg1;
            Timeline.SelectedSegment = seg1;

            CmbSubStreams.ItemsSource = _currentProbe.SubtitleStreams;
            CmbSubStreams.DisplayMemberPath = nameof(SubtitleStreamInfo.DisplayLabel);
            if (_currentProbe.SubtitleStreams.Count > 0)
                CmbSubStreams.SelectedIndex = 0;

            AppendLog($"[媒体信息] {_currentProbe.VideoCodec} {_currentProbe.Width}x{_currentProbe.Height} {_currentProbe.Fps:F2}fps | 音频: {(_currentProbe.HasAudio ? _currentProbe.AudioCodec : "无")} | 字幕轨: {_currentProbe.SubtitleStreams.Count}");

            _ = Task.Run(async () =>
            {
                try
                {
                    var kfs = await _engine.ProbeKeyframesAsync(path);
                    Dispatcher.Invoke(() =>
                    {
                        if (_currentVideoPath == path)
                        {
                            if (_currentProbe != null) _currentProbe.Keyframes = kfs;
                            Timeline.Keyframes = kfs;
                            AppendLog($"[关键帧] 已在时间轴标注 {kfs.Count} 个关键帧。");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"[提示] 关键帧扫描跳过: {ex.Message}"));
                }
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 读取媒体信息失败: {ex.Message}");
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        switch (e.Key)
        {
            case Key.Space:
                TogglePause();
                e.Handled = true;
                break;
            case Key.Left:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SeekRelative(-5);
                else StepFrame(-1);
                e.Handled = true;
                break;
            case Key.Right:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SeekRelative(5);
                else StepFrame(1);
                e.Handled = true;
                break;
            case Key.OemOpenBrackets:
                JumpKeyframe(-1);
                e.Handled = true;
                break;
            case Key.OemCloseBrackets:
                JumpKeyframe(1);
                e.Handled = true;
                break;
            case Key.I:
                SetCurrentSegmentIn();
                e.Handled = true;
                break;
            case Key.O:
                SetCurrentSegmentOut();
                e.Handled = true;
                break;
            case Key.N:
                AddNewSegment();
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteSelectedSegment();
                e.Handled = true;
                break;
        }
    }

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void BtnPrevFrame_Click(object sender, RoutedEventArgs e) => StepFrame(-1);
    private void BtnNextFrame_Click(object sender, RoutedEventArgs e) => StepFrame(1);
    private void BtnPrevKeyframe_Click(object sender, RoutedEventArgs e) => JumpKeyframe(-1);
    private void BtnNextKeyframe_Click(object sender, RoutedEventArgs e) => JumpKeyframe(1);
    private void BtnSetIn_Click(object sender, RoutedEventArgs e) => SetCurrentSegmentIn();
    private void BtnSetOut_Click(object sender, RoutedEventArgs e) => SetCurrentSegmentOut();
    private void BtnAddSegment_Click(object sender, RoutedEventArgs e) => AddNewSegment();
    private void BtnDeleteSegment_Click(object sender, RoutedEventArgs e) => DeleteSelectedSegment();

    private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
    {
        PanelSettings.Visibility = PanelSettings.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void JumpKeyframe(int direction)
    {
        var kfs = _currentProbe?.Keyframes;
        if (kfs == null || kfs.Count == 0) return;
        double cur = GetCurrentPosition();
        if (direction < 0)
        {
            double target = kfs.LastOrDefault(k => k < cur - 0.08);
            SeekAbsolute(target);
        }
        else
        {
            int idx = kfs.FindIndex(k => k > cur + 0.08);
            if (idx >= 0) SeekAbsolute(kfs[idx]);
        }
    }

    private void SetCurrentSegmentIn()
    {
        if (ListSegments.SelectedItem is not ClipSegment seg) return;
        double cur = GetCurrentPosition();
        seg.StartSeconds = cur;
        if (seg.EndSeconds <= seg.StartSeconds)
            seg.EndSeconds = Math.Min(Timeline.DurationSeconds, seg.StartSeconds + 5);
        Timeline.Refresh();
    }

    private void SetCurrentSegmentOut()
    {
        if (ListSegments.SelectedItem is not ClipSegment seg) return;
        double cur = GetCurrentPosition();
        seg.EndSeconds = Math.Max(cur, seg.StartSeconds + 0.1);
        Timeline.Refresh();
    }

    private void AddNewSegment()
    {
        if (Timeline.DurationSeconds <= 0) return;
        double cur = GetCurrentPosition();
        double end = Math.Min(Timeline.DurationSeconds, cur + 15);
        if (end - cur < 0.5) cur = Math.Max(0, Timeline.DurationSeconds - 5);

        var seg = new ClipSegment
        {
            Name = $"分段 {_segments.Count + 1}",
            StartSeconds = cur,
            EndSeconds = Math.Min(Timeline.DurationSeconds, Math.Max(end, cur + 1))
        };
        _segments.Add(seg);
        ListSegments.SelectedItem = seg;
        Timeline.SelectedSegment = seg;
        Timeline.Refresh();
    }

    private void DeleteSelectedSegment()
    {
        if (ListSegments.SelectedItem is not ClipSegment seg) return;
        _segments.Remove(seg);
        Timeline.SelectedSegment = _segments.FirstOrDefault();
        ListSegments.SelectedItem = Timeline.SelectedSegment;
        Timeline.Refresh();
    }

    private void ListSegments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Timeline.SelectedSegment = ListSegments.SelectedItem as ClipSegment;
    }

    private async void BtnExportMergedSegments_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentVideoPath) || _segments.Count == 0)
        {
            MessageBox.Show("请先打开视频并设置至少一个分段。");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "保存裁剪后的视频",
            FileName = Path.GetFileNameWithoutExtension(_currentVideoPath) + "_cut.mp4",
            Filter = "MP4 视频|*.mp4|MKV 视频|*.mkv|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        bool lossless = RbCutLossless.IsChecked == true;
        await RunTaskAsync("正在导出裁剪视频...", ct =>
            _engine.ExportSegmentsAsync(_currentVideoPath, _segments.ToList(), dlg.FileName,
                mergeIntoSingleFile: true, losslessKeyframeMode: lossless,
                useNvenc: UseNvenc(), quality: GetQuality(), AppendLog, ct));
    }

    private async void BtnExportSeparateSegments_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentVideoPath) || _segments.Count == 0)
        {
            MessageBox.Show("请先打开视频并设置至少一个分段。");
            return;
        }
        var dlg = new OpenFolderDialog { Title = "选择分段导出目录" };
        if (dlg.ShowDialog() != true) return;

        bool lossless = RbCutLossless.IsChecked == true;
        await RunTaskAsync("正在分别导出各分段...", ct =>
            _engine.ExportSegmentsAsync(_currentVideoPath, _segments.ToList(), dlg.FolderName,
                mergeIntoSingleFile: false, losslessKeyframeMode: lossless,
                useNvenc: UseNvenc(), quality: GetQuality(), AppendLog, ct));
    }

    private void BtnMergeAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要合并的视频（可多选）",
            Multiselect = true,
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.flv;*.ts;*.webm|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var f in dlg.FileNames) _mergeFiles.Add(f);
        }
    }

    private void BtnMergeUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = ListMergeFiles.SelectedIndex;
        if (idx > 0)
        {
            _mergeFiles.Move(idx, idx - 1);
            ListMergeFiles.SelectedIndex = idx - 1;
        }
    }

    private void BtnMergeDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = ListMergeFiles.SelectedIndex;
        if (idx >= 0 && idx < _mergeFiles.Count - 1)
        {
            _mergeFiles.Move(idx, idx + 1);
            ListMergeFiles.SelectedIndex = idx + 1;
        }
    }

    private void BtnMergeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ListMergeFiles.SelectedIndex >= 0)
            _mergeFiles.RemoveAt(ListMergeFiles.SelectedIndex);
    }

    private async void BtnMergeStart_Click(object sender, RoutedEventArgs e)
    {
        if (_mergeFiles.Count < 2)
        {
            MessageBox.Show("请至少添加 2 个视频文件。");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "保存合并后的视频",
            FileName = Path.GetFileNameWithoutExtension(_mergeFiles[0]) + "_merged.mp4",
            Filter = "MP4 视频|*.mp4|MKV 视频|*.mkv"
        };
        if (dlg.ShowDialog() != true) return;

        MergeStrategy strat = CmbMergeStrategy.SelectedIndex switch
        {
            1 => MergeStrategy.DemuxerCopy,
            2 => MergeStrategy.TsRelay,
            3 => MergeStrategy.UnifiedReencode,
            _ => MergeStrategy.Auto
        };

        await RunTaskAsync("正在合并视频...", ct =>
            _engine.MergeVideosAsync(_mergeFiles.ToList(), dlg.FileName, strat,
                UseNvenc(), GetQuality(), AppendLog, ct));
    }

    private async void BtnDemuxStart_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentVideoPath))
        {
            MessageBox.Show("请先打开一个视频文件。");
            return;
        }
        var dlg = new OpenFolderDialog { Title = "选择分离输出目录" };
        if (dlg.ShowDialog() != true) return;

        bool v = ChkDemuxVideo.IsChecked == true;
        bool a = ChkDemuxAudio.IsChecked == true;
        await RunTaskAsync("正在无损分离音视频...", ct =>
            _engine.DemuxAsync(_currentVideoPath, dlg.FolderName, v, a, AppendLog, ct));
    }

    private void BtnMuxPickVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.ts|所有文件|*.*" };
        if (dlg.ShowDialog() == true) TxtMuxVideo.Text = dlg.FileName;
    }

    private void BtnMuxPickAudio_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "音频文件|*.m4a;*.aac;*.mp3;*.wav;*.flac;*.opus;*.ogg|所有文件|*.*" };
        if (dlg.ShowDialog() == true) TxtMuxAudio.Text = dlg.FileName;
    }

    private async void BtnMuxStart_Click(object sender, RoutedEventArgs e)
    {
        string vid = TxtMuxVideo.Text.Trim();
        string aud = TxtMuxAudio.Text.Trim();
        if (!File.Exists(vid) || !File.Exists(aud))
        {
            MessageBox.Show("请同时选择有效的视频文件和音频文件。");
            return;
        }
        var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(vid) + "_muxed.mp4",
            Filter = "MP4 视频|*.mp4|MKV 视频|*.mkv"
        };
        if (dlg.ShowDialog() != true) return;

        bool shortest = ChkMuxShortest.IsChecked == true;
        bool toAac = ChkMuxAac.IsChecked == true;
        await RunTaskAsync("正在合并音视频...", ct =>
            _engine.MuxAvAsync(vid, aud, dlg.FileName, shortest, toAac, AppendLog, ct));
    }

    private async void BtnSubExtract_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentVideoPath) || CmbSubStreams.SelectedItem is not SubtitleStreamInfo sub)
        {
            MessageBox.Show("当前视频未检测到可提取的内封字幕轨。");
            return;
        }
        string ext = FFmpegEngine.SubExtForCodec(sub.CodecName);
        var dlg = new SaveFileDialog
        {
            FileName = $"{Path.GetFileNameWithoutExtension(_currentVideoPath)}_sub{sub.RelativeIndex}{ext}",
            Filter = "字幕文件|*.srt;*.ass;*.vtt|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        await RunTaskAsync("正在提取内封字幕...", ct =>
            _engine.ExtractSubtitleAsync(_currentVideoPath, sub.RelativeIndex, dlg.FileName, AppendLog, ct));
    }

    private void BtnSoftSubPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "字幕文件|*.srt;*.ass;*.ssa;*.vtt|所有文件|*.*" };
        if (dlg.ShowDialog() == true) TxtSoftSubPath.Text = dlg.FileName;
    }

    private async void BtnSoftSubMux_Click(object sender, RoutedEventArgs e)
    {
        string sub = TxtSoftSubPath.Text.Trim();
        if (string.IsNullOrEmpty(_currentVideoPath) || !File.Exists(sub))
        {
            MessageBox.Show("请先打开视频并选择外挂字幕文件。");
            return;
        }
        var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(_currentVideoPath) + "_softsub.mkv",
            Filter = "MKV 视频（推荐，保留ASS特效）|*.mkv|MP4 视频（自动转mov_text）|*.mp4"
        };
        if (dlg.ShowDialog() != true) return;

        string lang = TxtSoftSubLang.Text.Trim();
        await RunTaskAsync("正在软封装字幕...", ct =>
            _engine.SoftMuxSubtitleAsync(_currentVideoPath, sub, dlg.FileName, lang, AppendLog, ct));
    }

    private void BtnHardSubPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "字幕文件|*.srt;*.ass;*.ssa;*.vtt|所有文件|*.*" };
        if (dlg.ShowDialog() == true) TxtHardSubPath.Text = dlg.FileName;
    }

    private void BtnPreviewHardSub_Click(object sender, RoutedEventArgs e)
    {
        string sub = TxtHardSubPath.Text.Trim();
        if (!File.Exists(sub))
        {
            MessageBox.Show("请先选择字幕文件。");
            return;
        }
        if (_useMpv && _mpvHost != null)
        {
            _mpvHost.Player.AddSubtitle(sub);
            AppendLog($"[预览] 已在画面中实时加载字幕: {Path.GetFileName(sub)}");
        }
        else
        {
            MessageBox.Show("当前为 WPF 内置预览内核，放入 libmpv-2.dll 后重启即可实时预览字幕。");
        }
    }

    private async void BtnHardSubStart_Click(object sender, RoutedEventArgs e)
    {
        string sub = TxtHardSubPath.Text.Trim();
        if (string.IsNullOrEmpty(_currentVideoPath) || !File.Exists(sub))
        {
            MessageBox.Show("请先打开视频并选择字幕文件。");
            return;
        }
        var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(_currentVideoPath) + "_hardsub.mp4",
            Filter = "MP4 视频|*.mp4|MKV 视频|*.mkv"
        };
        if (dlg.ShowDialog() != true) return;

        bool copyAudio = ChkHardSubCopyAudio.IsChecked == true;
        await RunTaskAsync("正在硬写入字幕...", ct =>
            _engine.BurnSubtitleAsync(_currentVideoPath, sub, dlg.FileName,
                UseNvenc(), GetQuality(), copyAudio, AppendLog, ct));
    }

    private void SyncEnginePaths()
    {
        _engine.FFmpegPath = string.IsNullOrWhiteSpace(TxtFFmpegPath.Text) ? "ffmpeg" : TxtFFmpegPath.Text.Trim();
        _engine.FFprobePath = string.IsNullOrWhiteSpace(TxtFFprobePath.Text) ? "ffprobe" : TxtFFprobePath.Text.Trim();
    }

    private bool UseNvenc() => ChkNvenc.IsChecked == true;

    private int GetQuality() => int.TryParse(TxtQuality.Text.Trim(), out int q) ? q : 19;

    private void SetStatus(string text, Color dotColor)
    {
        TxtStatus.Text = text;
        StatusDot.Fill = new SolidColorBrush(dotColor);
    }

    private async Task RunTaskAsync(string statusMsg, Func<CancellationToken, Task> work)
    {
        if (_taskCts != null)
        {
            MessageBox.Show("当前已有任务正在运行，请等待完成或先点击停止。");
            return;
        }
        SyncEnginePaths();
        _taskCts = new CancellationTokenSource();
        SetStatus(statusMsg, Color.FromRgb(245, 158, 11));
        ProgressBar.IsIndeterminate = true;
        BtnCancelTask.IsEnabled = true;

        try
        {
            await work(_taskCts.Token);
            SetStatus("完成", Color.FromRgb(16, 185, 129));
        }
        catch (OperationCanceledException)
        {
            SetStatus("已中止", Color.FromRgb(148, 163, 184));
            AppendLog("[中止] 任务已取消。");
        }
        catch (Exception ex)
        {
            SetStatus("出错", Color.FromRgb(239, 68, 68));
            AppendLog($"[错误] {ex.Message}");
            if (TxtLog.Visibility != Visibility.Visible) ToggleLogVisibility();
            MessageBox.Show(ex.Message, "执行失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _taskCts.Dispose();
            _taskCts = null;
            ProgressBar.IsIndeterminate = false;
            BtnCancelTask.IsEnabled = false;
        }
    }

    private void BtnCancelTask_Click(object sender, RoutedEventArgs e) => _taskCts?.Cancel();
    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();
    private void BtnToggleLog_Click(object sender, RoutedEventArgs e) => ToggleLogVisibility();

    private void ToggleLogVisibility()
    {
        bool show = TxtLog.Visibility != Visibility.Visible;
        TxtLog.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnToggleLog.Content = show ? "收起日志 ▼" : "展开日志 ▲";
    }

    private void AppendLog(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TxtLog.AppendText(line + Environment.NewLine);
            TxtLog.ScrollToEnd();
        });
    }
}

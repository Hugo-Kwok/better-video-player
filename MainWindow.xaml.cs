using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VideoPlayer;

public partial class MainWindow : Window
{
    private const int DefaultSkipSeconds = 5;
    private const int MinSkipSeconds = 1;
    private const int MaxSkipSeconds = 600;

    // Rewind and fast-forward each keep their own skip amount.
    private int _rewindSeconds = DefaultSkipSeconds;
    private int _forwardSeconds = DefaultSkipSeconds;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _isPlaying;
    private bool _isDraggingSeek;
    private bool _hasMedia;
    private bool _controlsVisible = true;
    private double _volumeBeforeMute = 0.7;

    private string? _currentPath;

    // Playback engine (Windows' modern media engine, so Store codec extensions like AV1/HEVC work)
    private readonly ModernPlayer Player = null!;

    // Trim mode: selected range in seconds
    private const double MinTrimSeconds = 0.1;
    private bool _trimMode;
    private double _trimStart;
    private double _trimEnd;

    private bool _isFullscreen;
    private WindowState _prevState;
    private Rect _prevBounds;

    public MainWindow()
    {
        InitializeComponent();

        Player = new ModernPlayer(VideoImage);
        Player.MediaOpened += Player_MediaOpened;
        Player.MediaEnded += Player_MediaEnded;
        Player.MediaFailed += Player_MediaFailed;
        Closed += (_, _) => Player.Dispose();

        _timer.Tick += (_, _) => UpdatePosition();
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (_isPlaying && !ControlBar.IsMouseOver && !TopBar.IsMouseOver && Mouse.Captured == null) SetControlsVisible(false);
        };

        // Slider marks mouse-down as handled when it jumps to the click point,
        // so we must listen with handledEventsToo or we never see the click.
        SeekBar.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Seek_Down), true);

        Player.Volume = VolumeBar.Value;
        UpdateSkipLabels();
        UpdateVolumeIcon();

        // "Open with" / double-clicking a video passes its path as a command-line argument.
        Loaded += (_, _) => OpenFromCommandLine();
    }

    private void OpenFromCommandLine()
    {
        var path = Environment.GetCommandLineArgs().Skip(1)
            .Select(a => a.Trim('"'))
            .FirstOrDefault(System.IO.File.Exists);
        if (path != null) LoadFile(System.IO.Path.GetFullPath(path));
    }

    // ---------- Opening files ----------

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.webm;*.mpg;*.mpeg|All files|*.*"
        };
        if (dlg.ShowDialog() == true) LoadFile(dlg.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            LoadFile(files[0]);
    }

    private void LoadFile(string path)
    {
        StopFastPlayback();
        ExitTrimMode();
        CodecHelp.Visibility = Visibility.Collapsed;
        _currentPath = path;
        _hasMedia = false;
        Player.Open(path);
        Title = $"{System.IO.Path.GetFileNameWithoutExtension(path)} - Video Player";
        UpdateNavButtons();
        Hint.Visibility = Visibility.Collapsed;
        Play();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _hasMedia = true;
        if (Player.NaturalDuration.HasTimeSpan)
        {
            SeekBar.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
            TotalTime.Text = Format(Player.NaturalDuration.TimeSpan);
        }
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Pause();
        Player.Position = TimeSpan.Zero;
        UpdatePosition();
    }

    private async void Player_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        _hasMedia = false;
        Pause();
        HintText.Text = "Can't play this file";
        Hint.Visibility = Visibility.Visible;
        await ShowCodecHelpAsync(e.ErrorException);
    }

    // ---------- Missing-codec help ----------

    private record StoreLink(string Label, string ProductId);

    /// <summary>Video codecs Windows can only play with an extra Microsoft Store extension.</summary>
    private static (string Name, StoreLink[] Links)? VideoCodecFix(string codec) => codec switch
    {
        "hevc" => ("HEVC / H.265", new[]
        {
            new StoreLink("Get HEVC extension (free)", "9N4WGH0Z6VHQ"),
            new StoreLink("Paid version ($0.99)", "9NMZLZ57R3T7"),
        }),
        "vp9" or "vp8" => ("VP9", new[] { new StoreLink("Get VP9 extension", "9N4D0MSMP0PT") }),
        "av1" => ("AV1", new[] { new StoreLink("Get AV1 extension", "9MVZQVXJBQ9V") }),
        "mpeg2video" => ("MPEG-2", new[] { new StoreLink("Get MPEG-2 extension", "9N95Q1ZZPMH4") }),
        "theora" => ("Theora", new[] { new StoreLink("Get Web Media extension", "9N5TDP8VCMHS") }),
        _ => null,
    };

    /// <summary>Audio codecs Windows can't (or may not) decode, with an explanation.</summary>
    private static string? AudioCodecProblem(string codec) => codec switch
    {
        "dts" => "The audio is DTS. Windows has no built-in DTS decoder and there is no Microsoft Store extension for it.",
        "truehd" or "mlp" => "The audio is Dolby TrueHD, which Windows can't decode.",
        "ac3" or "eac3" => "The audio is Dolby Digital (AC-3 / E-AC-3). Windows 11 24H2 and later no longer include this decoder on fresh installs, unless your PC maker added it.",
        "vorbis" => "The audio is Vorbis, which needs the Web Media extension.",
        _ => null,
    };

    private async Task ShowCodecHelpAsync(Exception? error)
    {
        var file = _currentPath;
        CodecButtons.Children.Clear();

        string? video = null, audio = null;
        var ffmpeg = FFmpeg.FindSilently();
        if (ffmpeg != null && file != null) (video, audio) = await FFmpeg.ProbeCodecsAsync(ffmpeg, file);
        if (file != _currentPath) return; // user already moved on to another file

        var lines = new List<string>();
        var links = new List<StoreLink>();
        if (video == null && audio == null)
        {
            lines.Add("Windows doesn't have a decoder for this file's video or audio format.");
            if (!string.IsNullOrWhiteSpace(error?.Message)) lines.Add(error!.Message.Trim());
            if (ffmpeg == null) lines.Add("(Install FFmpeg to see exactly which codec is missing.)");
        }
        else
        {
            lines.Add($"Video: {video ?? "none"}   ·   Audio: {audio ?? "none"}");
            if (video != null && VideoCodecFix(video) is { } fix)
            {
                lines.Add($"The video is {fix.Name}, which needs a Microsoft Store extension. Install it, then click Try again.");
                links.AddRange(fix.Links);
            }
            if (audio != null && AudioCodecProblem(audio) is { } problem)
            {
                lines.Add(problem);
                if (audio == "vorbis") links.Add(new StoreLink("Get Web Media extension", "9N5TDP8VCMHS"));
            }
            if (lines.Count == 1)
                lines.Add("Windows doesn't seem to have a decoder for one of these formats.");
        }

        CodecDetails.Text = string.Join("\n\n", lines);
        foreach (var link in links.DistinctBy(l => l.ProductId))
            CodecButtons.Children.Add(MakeHelpButton(link.Label, () => OpenStore(link.ProductId), accent: true));
        CodecButtons.Children.Add(MakeHelpButton("Try again", () => { if (_currentPath != null) LoadFile(_currentPath); }, accent: false));
        CodecHelp.Visibility = Visibility.Visible;
    }

    private Button MakeHelpButton(string text, Action onClick, bool accent)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource(accent ? "AccentButton" : "TextButton"),
            Margin = new Thickness(4),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void OpenStore(string productId)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"ms-windows-store://pdp/?ProductId={productId}") { UseShellExecute = true });
        }
        catch
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://apps.microsoft.com/detail/{productId}") { UseShellExecute = true });
        }
    }

    // ---------- Transport ----------

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    // Clicking the video no longer pauses. Holding it for 0.2 s plays forwards (right half)
    // or backwards (left half) at the fast-forward / fast-rewind button speed until released.
    // Double-click still toggles full screen.
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _holdTimerHooked;
    private FastMode _pendingHoldMode;

    private void Video_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            CancelVideoHold();
            ToggleFullscreen();
            return;
        }
        if (!_hasMedia) return;

        _pendingHoldMode = e.GetPosition(ClickLayer).X >= ClickLayer.ActualWidth / 2 ? FastMode.Forward : FastMode.Backward;
        if (!_holdTimerHooked)
        {
            _holdTimer.Tick += (_, _) =>
            {
                _holdTimer.Stop();
                if (Mouse.LeftButton == MouseButtonState.Pressed && ClickLayer.IsMouseCaptured)
                    StartFastPlayback(_pendingHoldMode);
            };
            _holdTimerHooked = true;
        }
        ClickLayer.CaptureMouse(); // keep receiving the release even if the pointer leaves the video
        _holdTimer.Start();
    }

    private void Video_MouseUp(object sender, MouseButtonEventArgs e)
    {
        CancelVideoHold();
        e.Handled = true;
    }

    private void Video_LostCapture(object sender, MouseEventArgs e)
    {
        _holdTimer.Stop();
        StopFastPlayback();
    }

    private void CancelVideoHold()
    {
        _holdTimer.Stop();
        StopFastPlayback();
        if (ClickLayer.IsMouseCaptured) ClickLayer.ReleaseMouseCapture();
    }

    private void TogglePlay()
    {
        if (!_hasMedia && Player.Source == null) return;
        if (_isPlaying) Pause(); else Play();
    }

    private void Play()
    {
        Player.Play();
        _timer.Start();
        _isPlaying = true;
        PlayIcon.Text = ""; // pause glyph
        ShowControlsTemporarily();
    }

    private void Pause()
    {
        Player.Pause();
        _timer.Stop();
        _isPlaying = false;
        PlayIcon.Text = ""; // play glyph
        _hideTimer.Stop();
        SetControlsVisible(true);
    }

    private void Rewind_Click(object sender, RoutedEventArgs e) => SkipBy(-_rewindSeconds);
    private void Forward_Click(object sender, RoutedEventArgs e) => SkipBy(_forwardSeconds);

    private void SkipBy(int seconds)
    {
        if (!_hasMedia) return;
        var target = Player.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (Player.NaturalDuration.HasTimeSpan && target > Player.NaturalDuration.TimeSpan)
            target = Player.NaturalDuration.TimeSpan;
        Player.Position = target;
        UpdatePosition();
    }

    // ---------- Hold-to-play fast forward / backward ----------

    private static readonly double[] FastSpeeds = { 1.5, 2, 3, 4, 8, 16 };
    // Reverse playback also offers slow speeds, down to a quarter speed.
    private static readonly double[] ReverseSpeeds = { 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 8, 16 };
    private double _fastForwardSpeed = 2;
    private double _fastRewindSpeed = 2;

    private enum FastMode { None, Forward, Backward }
    private FastMode _fastMode;
    private bool _wasPlayingBeforeFast;
    private readonly DispatcherTimer _reverseTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly System.Diagnostics.Stopwatch _reverseClock = new();
    private bool _reverseTimerHooked;

    private void FastForward_Down(object sender, MouseButtonEventArgs e) => StartFastPlayback(FastMode.Forward);
    private void FastRewind_Down(object sender, MouseButtonEventArgs e) => StartFastPlayback(FastMode.Backward);
    private void Fast_Up(object sender, MouseButtonEventArgs e) => StopFastPlayback();
    private void Fast_LostCapture(object sender, MouseEventArgs e) => StopFastPlayback();

    private void StartFastPlayback(FastMode mode)
    {
        if (!_hasMedia || _fastMode != FastMode.None) return;
        _fastMode = mode;
        _wasPlayingBeforeFast = _isPlaying;

        if (mode == FastMode.Forward)
        {
            Player.PlaybackRate = _fastForwardSpeed;
            if (!_isPlaying) Play();
        }
        else
        {
            // The media engine can't decode backwards, so jump back in small steps instead.
            if (_isPlaying) Pause();
            if (!_reverseTimerHooked)
            {
                _reverseTimer.Tick += (_, _) => ReverseTick();
                _reverseTimerHooked = true;
            }
            _reverseClock.Restart();
            _reverseTimer.Start();
        }
    }

    private void ReverseTick()
    {
        double elapsed = _reverseClock.Elapsed.TotalSeconds;
        _reverseClock.Restart();
        var target = Player.Position - TimeSpan.FromSeconds(elapsed * _fastRewindSpeed);
        if (target <= TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
            _reverseTimer.Stop(); // reached the start; stay there until the button is released
        }
        Player.Position = target;
        UpdatePosition();
    }

    private void StopFastPlayback()
    {
        if (_fastMode == FastMode.None) return;
        var mode = _fastMode;
        _fastMode = FastMode.None;

        if (mode == FastMode.Forward)
        {
            Player.PlaybackRate = 1;
            if (!_wasPlayingBeforeFast && _isPlaying) Pause();
        }
        else
        {
            _reverseTimer.Stop();
            if (_wasPlayingBeforeFast && _hasMedia) Play();
        }
    }

    private void FastForward_Wheel(object sender, MouseWheelEventArgs e)
    {
        _fastForwardSpeed = NextSpeed(FastSpeeds, _fastForwardSpeed, e.Delta > 0);
        if (_fastMode == FastMode.Forward) Player.PlaybackRate = _fastForwardSpeed;
        UpdateFastLabels();
        e.Handled = true;
    }

    private void FastRewind_Wheel(object sender, MouseWheelEventArgs e)
    {
        _fastRewindSpeed = NextSpeed(ReverseSpeeds, _fastRewindSpeed, e.Delta > 0);
        UpdateFastLabels();
        e.Handled = true;
    }

    private static double NextSpeed(double[] speeds, double current, bool up)
    {
        int i = Array.IndexOf(speeds, current);
        if (i < 0) i = Array.IndexOf(speeds, 2.0);
        return speeds[Math.Clamp(i + (up ? 1 : -1), 0, speeds.Length - 1)];
    }

    private void UpdateFastLabels()
    {
        FastForwardLabel.Text = $"x{_fastForwardSpeed:0.##}";
        FastRewindLabel.Text = $"x{_fastRewindSpeed:0.##}";
    }

    // ---------- Frame stepping ----------

    private async void StepFrame(bool forward)
    {
        if (!_hasMedia) return;
        if (_isPlaying) Pause();
        await Player.StepFrameAsync(forward);
        UpdatePosition();
    }

    // ---------- Scroll-to-adjust skip amount (each button independently) ----------

    private void Rewind_Wheel(object sender, MouseWheelEventArgs e)
    {
        _rewindSeconds = Math.Clamp(_rewindSeconds + (e.Delta > 0 ? 1 : -1), MinSkipSeconds, MaxSkipSeconds);
        UpdateSkipLabels();
        e.Handled = true;
    }

    private void Forward_Wheel(object sender, MouseWheelEventArgs e)
    {
        _forwardSeconds = Math.Clamp(_forwardSeconds + (e.Delta > 0 ? 1 : -1), MinSkipSeconds, MaxSkipSeconds);
        UpdateSkipLabels();
        e.Handled = true;
    }

    private void UpdateSkipLabels()
    {
        RewindLabel.Text = _rewindSeconds.ToString();
        ForwardLabel.Text = _forwardSeconds.ToString();
    }

    // ---------- Seek bar ----------

    private void Seek_Down(object sender, MouseButtonEventArgs e) => _isDraggingSeek = true;

    // Handled at window level so releasing the mouse anywhere ends the seek.
    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingSeek) return;
        _isDraggingSeek = false;
        if (_hasMedia)
        {
            Player.Position = TimeSpan.FromSeconds(SeekBar.Value);
            CurrentTime.Text = Format(Player.Position);
        }
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDraggingSeek) CurrentTime.Text = Format(TimeSpan.FromSeconds(SeekBar.Value));
    }

    private void UpdatePosition()
    {
        if (_isDraggingSeek) return;
        SeekBar.Value = Math.Min(Player.Position.TotalSeconds, SeekBar.Maximum);
        CurrentTime.Text = Format(Player.Position);
    }

    // ---------- Previous / next video in the folder ----------

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts", ".flv", ".3gp",
    };

    // Same "natural" ordering File Explorer uses (video2 before video10).
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Videos in the current file's folder, in File Explorer order, plus the current file's index.</summary>
    private (List<string> files, int index) GetFolderVideos()
    {
        if (_currentPath == null) return (new List<string>(), -1);
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_currentPath)!;
            var files = System.IO.Directory.EnumerateFiles(dir)
                .Where(f => VideoExtensions.Contains(System.IO.Path.GetExtension(f)))
                .ToList();
            files.Sort((a, b) => StrCmpLogicalW(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
            return (files, files.FindIndex(f => string.Equals(f, _currentPath, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return (new List<string>(), -1);
        }
    }

    private void UpdateNavButtons()
    {
        var (files, i) = GetFolderVideos();
        bool hasPrev = i > 0, hasNext = i >= 0 && i < files.Count - 1;
        PrevBtn.Visibility = hasPrev ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Visibility = hasNext ? Visibility.Visible : Visibility.Collapsed;
        PrevBtn.ToolTip = hasPrev ? $"Previous: {System.IO.Path.GetFileName(files[i - 1])} (Page Up)" : null;
        NextBtn.ToolTip = hasNext ? $"Next: {System.IO.Path.GetFileName(files[i + 1])} (Page Down)" : null;
    }

    private void PrevVideo_Click(object sender, RoutedEventArgs e) => PlaySibling(-1);
    private void NextVideo_Click(object sender, RoutedEventArgs e) => PlaySibling(+1);

    private void PlaySibling(int direction)
    {
        var (files, i) = GetFolderVideos(); // re-read in case the folder changed
        int target = i + direction;
        if (i < 0 || target < 0 || target >= files.Count) { UpdateNavButtons(); return; }
        LoadFile(files[target]);
    }

    // ---------- Move to Recycle Bin ----------

    private async void DeleteVideo_Click(object sender, RoutedEventArgs e)
    {
        var path = _currentPath;
        if (path == null) return;
        if (_isPlaying) Pause();

        var answer = MessageBox.Show(this,
            $"Move this video to the Recycle Bin?\n\n{System.IO.Path.GetFileName(path)}",
            "Move to Recycle Bin", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        // Remember the neighbours before the file disappears from the folder listing.
        var (files, index) = GetFolderVideos();
        string? next = index >= 0 && index + 1 < files.Count ? files[index + 1]
                     : index > 0 ? files[index - 1] : null;

        // Release the file so Windows can move it.
        ExitTrimMode();
        _hasMedia = false;
        Player.Close();

        bool moved = false;
        for (int attempt = 0; attempt < 6 && !moved; attempt++)
        {
            if (attempt > 0) await Task.Delay(200); // the engine may take a moment to let go of the file
            moved = MoveToRecycleBin(path);
        }

        if (!moved)
        {
            MessageBox.Show(this, "Couldn't move the video to the Recycle Bin. It may be open in another program.",
                "Move to Recycle Bin", MessageBoxButton.OK, MessageBoxImage.Error);
            LoadFile(path);
            return;
        }

        if (next != null && System.IO.File.Exists(next)) LoadFile(next);
        else ShowEmptyState();
    }

    private void ShowEmptyState()
    {
        _currentPath = null;
        _hasMedia = false;
        Title = "Video Player";
        SeekBar.Maximum = 1;
        SeekBar.Value = 0;
        CurrentTime.Text = TotalTime.Text = "0:00";
        HintText.Text = "Open or drop a video file";
        Hint.Visibility = Visibility.Visible;
        CodecHelp.Visibility = Visibility.Collapsed;
        UpdateNavButtons();
    }

    // Shell file operation: moves the file to the Recycle Bin (undoable), without Windows' own confirmation dialog.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private bool MoveToRecycleBin(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = new WindowInteropHelper(this).Handle,
            wFunc = FO_DELETE,
            pFrom = path + "\0", // list must end with two nulls; the marshaller adds the second
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !System.IO.File.Exists(path);
    }

    // ---------- Trim mode ----------

    private void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (_trimMode) ExitTrimMode(); else EnterTrimMode();
    }

    private void EnterTrimMode()
    {
        if (!_hasMedia || SeekBar.Maximum <= MinTrimSeconds) return;
        _trimMode = true;
        // Start the selection at the current position (leaving room for the minimum length).
        _trimStart = Math.Clamp(Player.Position.TotalSeconds, 0, Math.Max(0, SeekBar.Maximum - MinTrimSeconds));
        _trimEnd = SeekBar.Maximum;
        TrimOverlay.Visibility = Visibility.Visible;
        TrimPanel.Visibility = Visibility.Visible;
        TrimIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD4, 0x00));
        UpdateTrimVisuals();
        ShowControlsTemporarily();
    }

    private void ExitTrimMode()
    {
        if (!_trimMode) return;
        _trimMode = false;
        TrimOverlay.Visibility = Visibility.Collapsed;
        TrimPanel.Visibility = Visibility.Collapsed;
        TrimIcon.ClearValue(TextBlock.ForegroundProperty);
    }

    private void TrimCancel_Click(object sender, RoutedEventArgs e) => ExitTrimMode();

    private void SetTrimStart_Click(object sender, RoutedEventArgs e) => SetTrimStart(Player.Position.TotalSeconds);
    private void SetTrimEnd_Click(object sender, RoutedEventArgs e) => SetTrimEnd(Player.Position.TotalSeconds);

    private void SetTrimStart(double seconds)
    {
        if (!_trimMode) return;
        _trimStart = Math.Clamp(seconds, 0, Math.Max(0, _trimEnd - MinTrimSeconds));
        UpdateTrimVisuals();
    }

    private void SetTrimEnd(double seconds)
    {
        if (!_trimMode) return;
        _trimEnd = Math.Clamp(seconds, Math.Min(SeekBar.Maximum, _trimStart + MinTrimSeconds), SeekBar.Maximum);
        UpdateTrimVisuals();
    }

    // The slider's thumb is 14px wide, so its centre travels from x=7 to x=width-7.
    private double TrackWidth => Math.Max(1, TrimOverlay.ActualWidth - 14);
    private double ValueToX(double seconds) => 7 + seconds / Math.Max(SeekBar.Maximum, 0.001) * TrackWidth;

    private void UpdateTrimVisuals()
    {
        if (!_trimMode) return;
        double x1 = ValueToX(_trimStart), x2 = ValueToX(_trimEnd);
        Canvas.SetLeft(TrimRange, x1);
        TrimRange.Width = Math.Max(0, x2 - x1);
        Canvas.SetLeft(TrimStartThumb, x1 - TrimStartThumb.Width / 2);
        Canvas.SetLeft(TrimEndThumb, x2 - TrimEndThumb.Width / 2);
        TrimInfo.Text = $"{FormatPrecise(TimeSpan.FromSeconds(_trimStart))} – {FormatPrecise(TimeSpan.FromSeconds(_trimEnd))}" +
                        $"   ({_trimEnd - _trimStart:0.0} s)";
    }

    private void TrimOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTrimVisuals();

    private void TrimThumb_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        if (_isPlaying) Pause(); // so the preview frame follows the handle
    }

    private void TrimStart_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        SetTrimStart(_trimStart + e.HorizontalChange / TrackWidth * SeekBar.Maximum);
        PreviewAt(_trimStart);
    }

    private void TrimEnd_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        SetTrimEnd(_trimEnd + e.HorizontalChange / TrackWidth * SeekBar.Maximum);
        PreviewAt(_trimEnd);
    }

    private void PreviewAt(double seconds)
    {
        Player.Position = TimeSpan.FromSeconds(seconds);
        UpdatePosition();
    }

    private async void TrimExport_Click(object sender, RoutedEventArgs e)
    {
        if (!_trimMode || _currentPath == null) return;
        if (_isPlaying) Pause();

        var ffmpeg = FFmpeg.Locate(this);
        if (ffmpeg == null) return;

        VideoInfo? info;
        Mouse.OverrideCursor = Cursors.Wait;
        try { info = await FFmpeg.ProbeAsync(ffmpeg, _currentPath); }
        finally { Mouse.OverrideCursor = null; }
        info ??= new VideoInfo(Player.NaturalVideoWidth, Player.NaturalVideoHeight, 0, "", 0);

        new ExportWindow(_currentPath, _trimStart, _trimEnd, info, ffmpeg) { Owner = this }.ShowDialog();
    }

    // ---------- Volume ----------

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Player == null) return; // fires once during XAML load
        Player.Volume = e.NewValue;
        UpdateVolumeIcon();
    }

    private void Volume_Wheel(object sender, MouseWheelEventArgs e)
    {
        VolumeBar.Value = Math.Clamp(VolumeBar.Value + (e.Delta > 0 ? 0.05 : -0.05), 0, 1);
        e.Handled = true;
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeBar.Value > 0)
        {
            _volumeBeforeMute = VolumeBar.Value;
            VolumeBar.Value = 0;
        }
        else
        {
            VolumeBar.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : 0.7;
        }
    }

    private void UpdateVolumeIcon()
    {
        if (VolumeIcon == null) return;
        VolumeIcon.Text = VolumeBar.Value <= 0 ? "" : ""; // mute : volume
    }

    // ---------- Auto-hiding controls ----------

    private void Window_MouseMove(object sender, MouseEventArgs e) => ShowControlsTemporarily();

    private void ShowControlsTemporarily()
    {
        SetControlsVisible(true);
        _hideTimer.Stop();
        if (_isPlaying) _hideTimer.Start();
    }

    private void SetControlsVisible(bool visible)
    {
        Mouse.OverrideCursor = visible ? null : Cursors.None;
        if (_controlsVisible == visible) return;
        _controlsVisible = visible;
        foreach (var el in new UIElement[] { ControlBar, TopBar, SideNav })
        {
            el.IsHitTestVisible = visible;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(200)));
        }
    }

    // ---------- Custom title bar ----------

    private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isFullscreen) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized && !_isFullscreen;
        MaxIcon.Text = maximized ? "" : ""; // restore : maximize
        // A borderless maximized window hangs a few pixels off each screen edge; pad it back in.
        Root.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
    }

    // ---------- Full screen ----------

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (!_isFullscreen)
        {
            _isFullscreen = true;
            _prevState = WindowState;
            _prevBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

            var screen = GetMonitorBounds();
            WindowState = WindowState.Normal;
            Topmost = true;
            Left = screen.Left; Top = screen.Top; Width = screen.Width; Height = screen.Height;
            if (chrome != null) chrome.ResizeBorderThickness = new Thickness(0);
            CaptionButtons.Visibility = Visibility.Collapsed;
            Root.Margin = new Thickness(0);
            FullIcon.Text = ""; // back to window
        }
        else
        {
            _isFullscreen = false;
            Topmost = false;
            Left = _prevBounds.Left; Top = _prevBounds.Top; Width = _prevBounds.Width; Height = _prevBounds.Height;
            if (chrome != null) chrome.ResizeBorderThickness = new Thickness(6);
            CaptionButtons.Visibility = Visibility.Visible;
            WindowState = _prevState;
            Window_StateChanged(this, EventArgs.Empty);
            FullIcon.Text = ""; // full screen
        }
    }

    // Bounds (in WPF units) of the monitor the window is on, including the taskbar area.
    private Rect GetMonitorBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref info))
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var dpi = VisualTreeHelper.GetDpi(this);
        var r = info.rcMonitor;
        return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY,
                        (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    // ---------- Keyboard ----------

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); e.Handled = true; break;
            case Key.Left: SkipBy(-_rewindSeconds); e.Handled = true; break;
            case Key.Right: SkipBy(_forwardSeconds); e.Handled = true; break;
            case Key.Up: VolumeBar.Value = Math.Min(1, VolumeBar.Value + 0.05); e.Handled = true; break;
            case Key.Down: VolumeBar.Value = Math.Max(0, VolumeBar.Value - 0.05); e.Handled = true; break;
            case Key.M: Mute_Click(this, e); e.Handled = true; break;
            case Key.F11: ToggleFullscreen(); e.Handled = true; break;
            case Key.Escape:
                if (_isFullscreen) ToggleFullscreen();
                else ExitTrimMode();
                e.Handled = true;
                break;
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control: Open_Click(this, e); e.Handled = true; break;
            case Key.OemComma: StepFrame(forward: false); e.Handled = true; break;
            case Key.OemPeriod: StepFrame(forward: true); e.Handled = true; break;
            case Key.PageUp: PlaySibling(-1); e.Handled = true; break;
            case Key.PageDown: PlaySibling(+1); e.Handled = true; break;
            case Key.T: if (_trimMode) ExitTrimMode(); else EnterTrimMode(); e.Handled = true; break;
            case Key.OemOpenBrackets or Key.I: SetTrimStart(Player.Position.TotalSeconds); e.Handled = true; break;
            case Key.OemCloseBrackets or Key.O: SetTrimEnd(Player.Position.TotalSeconds); e.Handled = true; break;
        }
        ShowControlsTemporarily();
    }

    /// <summary>Like Format but with tenths of a second, for trim points.</summary>
    public static string FormatPrecise(TimeSpan t) =>
        (t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}")
        + $".{t.Milliseconds / 100}";

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}

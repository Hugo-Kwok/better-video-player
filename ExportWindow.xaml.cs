using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VideoPlayer;

public partial class ExportWindow : Window
{
    private static readonly string[] KnownFormats = { "mp4", "mkv", "mov", "webm", "avi", "wmv", "m4v", "mpg" };

    private readonly string _source;
    private readonly double _start;
    private readonly double _duration;
    private readonly VideoInfo _info;
    private readonly string _ffmpeg;

    private HashSet<string> _gpuEncoders = new();
    private bool _populatingEncoders;
    private bool _encoderTouched;

    private Process? _proc;
    private string? _outPath;
    private bool _cancelled;
    private bool _done;
    private string _statusPrefix = "Exporting";

    public ExportWindow(string source, double start, double end, VideoInfo info, string ffmpeg)
    {
        InitializeComponent();
        _source = source;
        _start = start;
        _duration = end - start;
        _info = info;
        _ffmpeg = ffmpeg;

        ClipInfo.Text = $"{Path.GetFileName(source)}\n{MainWindow.FormatPrecise(TimeSpan.FromSeconds(start))} – " +
                        $"{MainWindow.FormatPrecise(TimeSpan.FromSeconds(end))}  ({_duration:0.0} s)";

        // Format: source container first (default)
        var srcExt = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
        if (srcExt == "mpeg") srcExt = "mpg";
        if (string.IsNullOrEmpty(srcExt)) srcExt = "mp4";
        Add(FormatBox, $"Same as source (.{srcExt})", srcExt);
        foreach (var f in KnownFormats.Where(f => f != srcExt))
            Add(FormatBox, $"{f.ToUpperInvariant()} (.{f})", f);

        // Resolution: source first, then standard heights below it
        Add(ResolutionBox, info.Height > 0 ? $"Same as source ({info.Width} × {info.Height})" : "Same as source", 0);
        foreach (var h in new[] { 2160, 1440, 1080, 720, 480, 360, 240 }.Where(h => info.Height == 0 || h < info.Height))
            Add(ResolutionBox, $"{h}p", h);

        // Quality
        Add(QualityBox, info.BitRate > 0 ? $"Same as source (~{info.BitRate / 1_000_000.0:0.#} Mbps)" : "Same as source (high)", "source");
        Add(QualityBox, "High", "high");
        Add(QualityBox, "Medium (smaller file)", "medium");
        Add(QualityBox, "Low (smallest file)", "low");

        // Frame rate: source first, then common rates not above it
        Add(FpsBox, info.Fps > 0 ? $"Same as source ({info.Fps:0.##} fps)" : "Same as source", 0.0);
        foreach (var r in new[] { 60.0, 50, 30, 25, 24, 15 }.Where(r => info.Fps <= 0 || r < info.Fps - 0.5))
            Add(FpsBox, $"{r:0} fps", r);

        foreach (var box in new[] { FormatBox, ResolutionBox, QualityBox, FpsBox }) box.SelectedIndex = 0;

        // Encoder: CPU until GPU detection finishes, then the first working GPU becomes the default.
        _populatingEncoders = true;
        Add(EncoderBox, "CPU (software)", "cpu");
        EncoderBox.Items.Add(new ComboBoxItem { Content = "Checking for GPU encoders…", IsEnabled = false, Tag = "checking" });
        EncoderBox.SelectedIndex = 0;
        _populatingEncoders = false;
        UpdateEncoderAvailability();
        Loaded += async (_, _) => await LoadGpuEncodersAsync();

        // Fast copy is the default: instant and lossless.
        CopyBox.IsChecked = true;
    }

    private static readonly (string Tag, string Label)[] GpuVendors =
    {
        ("nvenc", "NVIDIA GPU (NVENC)"),
        ("qsv", "Intel GPU (Quick Sync)"),
        ("amf", "AMD GPU (AMF)"),
    };

    private async Task LoadGpuEncodersAsync()
    {
        _gpuEncoders = await FFmpeg.DetectGpuEncodersAsync(_ffmpeg);

        _populatingEncoders = true;
        foreach (var item in EncoderBox.Items.OfType<ComboBoxItem>().Where(i => (string)i.Tag == "checking").ToList())
            EncoderBox.Items.Remove(item);
        int insertAt = 0;
        foreach (var (tag, label) in GpuVendors)
            if (_gpuEncoders.Contains($"h264_{tag}"))
                EncoderBox.Items.Insert(insertAt++, new ComboBoxItem { Content = label, Tag = tag });
        if (insertAt == 0)
            EncoderBox.Items.Add(new ComboBoxItem { Content = "No supported GPU encoder found", IsEnabled = false, Tag = "none" });
        if (!_encoderTouched) EncoderBox.SelectedIndex = 0; // default to the GPU if there is one
        _populatingEncoders = false;
        UpdateEncoderAvailability();
    }

    private void EncoderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_populatingEncoders) _encoderTouched = true;
    }

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEncoderAvailability();

    /// <summary>GPU encoding applies to H.264/H.265 output; other formats (WebM, WMV, MPG) and fast copy use the CPU path.</summary>
    private bool GpuApplies()
    {
        if (CopyBox?.IsChecked == true || FormatBox.SelectedItem == null) return false;
        var v = CodecsFor(Selected<string>(FormatBox), _info.VideoCodec).video;
        return v is "libx264" or "libx265";
    }

    private void UpdateEncoderAvailability()
    {
        if (EncoderBox == null || CopyBox == null) return; // during InitializeComponent
        bool applies = GpuApplies();
        EncoderBox.IsEnabled = applies && Options.IsEnabled;
        EncoderBox.ToolTip = applies ? null
            : CopyBox.IsChecked == true ? "Fast copy doesn't re-encode, so no encoder is used."
            : "GPU encoding is used for MP4, MKV, MOV, M4V and AVI. This format is encoded on the CPU.";
    }

    /// <summary>"nvenc" / "qsv" / "amf", or "cpu".</summary>
    private string ChosenEncoder()
    {
        if (!GpuApplies() || EncoderBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return "cpu";
        return GpuVendors.Any(g => g.Tag == tag) ? tag : "cpu";
    }

    private static void Add(ComboBox box, string label, object tag) =>
        box.Items.Add(new ComboBoxItem { Content = label, Tag = tag });

    private static T Selected<T>(ComboBox box) => (T)((ComboBoxItem)box.SelectedItem).Tag;

    private void CopyBox_Changed(object sender, RoutedEventArgs e)
    {
        bool copy = CopyBox.IsChecked == true;
        ResolutionBox.IsEnabled = QualityBox.IsEnabled = FpsBox.IsEnabled = !copy;
        UpdateEncoderAvailability();
    }

    // ---------- Export ----------

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            // After a successful export this button becomes "Show in folder"
            Process.Start("explorer.exe", $"/select,\"{_outPath}\"");
            return;
        }

        var ext = Selected<string>(FormatBox);
        var dlg = new SaveFileDialog
        {
            Title = "Save clip as",
            InitialDirectory = Path.GetDirectoryName(_source),
            FileName = $"{Path.GetFileNameWithoutExtension(_source)}_trimmed.{ext}",
            DefaultExt = ext,
            Filter = $"{ext.ToUpperInvariant()} video|*.{ext}",
        };
        if (dlg.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_source), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Please choose a different file name - the source video can't be overwritten while it's open.",
                "Export clip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _outPath = dlg.FileName;

        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in BuildArgs(ext, _outPath)) psi.ArgumentList.Add(a);

        Options.IsEnabled = CopyBox.IsEnabled = ExportBtn.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        Progress.Value = 0;
        var hw = CopyBox.IsChecked == true ? "copy" : ChosenEncoder();
        _statusPrefix = hw switch
        {
            "copy" => "Copying",
            "cpu" => "Exporting on CPU",
            _ => $"Exporting on {GpuVendors.First(g => g.Tag == hw).Label}",
        };
        StatusText.Text = _statusPrefix + "…";

        var errorTail = new Queue<string>();
        _cancelled = false;
        int exitCode;
        try
        {
            _proc = new Process { StartInfo = psi };
            _proc.OutputDataReceived += (_, ev) => OnProgressLine(ev.Data);
            _proc.ErrorDataReceived += (_, ev) =>
            {
                if (ev.Data == null) return;
                lock (errorTail) { errorTail.Enqueue(ev.Data); while (errorTail.Count > 15) errorTail.Dequeue(); }
            };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            await _proc.WaitForExitAsync();
            exitCode = _proc.ExitCode;
        }
        catch (Exception ex)
        {
            exitCode = -1;
            lock (errorTail) errorTail.Enqueue(ex.Message);
        }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }

        if (_cancelled)
        {
            TryDelete(_outPath);
            if (!IsLoaded) return; // window was closed
            StatusText.Text = "Export cancelled.";
            Progress.Value = 0;
            Options.IsEnabled = CopyBox.IsEnabled = ExportBtn.IsEnabled = true;
            CopyBox_Changed(this, e);
            CancelBtn.Content = "Cancel";
            return;
        }

        if (exitCode == 0)
        {
            _done = true;
            Progress.Value = 100;
            StatusText.Text = $"Saved to {_outPath}";
            ExportBtn.Content = "Show in folder";
            ExportBtn.IsEnabled = true;
            CancelBtn.Content = "Close";
        }
        else
        {
            TryDelete(_outPath);
            string details;
            lock (errorTail) details = string.Join("\n", errorTail);
            StatusText.Text = "Export failed.";
            var hint = ChosenEncoder() != "cpu" && CopyBox.IsChecked != true
                ? "Tip: try again with Encoder set to \"CPU (software)\".\n\n" : "";
            MessageBox.Show(this, "FFmpeg couldn't export this clip.\n\n" + hint + details, "Export failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Options.IsEnabled = CopyBox.IsEnabled = ExportBtn.IsEnabled = true;
            CopyBox_Changed(this, e);
        }
    }

    private void OnProgressLine(string? line)
    {
        if (line == null) return;
        double seconds;
        if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms=")) // both are microseconds
        {
            if (!long.TryParse(line[(line.IndexOf('=') + 1)..], out var us)) return;
            seconds = us / 1_000_000.0;
        }
        else if (line == "progress=end") seconds = _duration;
        else return;

        var pct = _duration > 0 ? Math.Clamp(seconds / _duration * 100, 0, 100) : 0;
        Dispatcher.BeginInvoke(() =>
        {
            if (_done || _cancelled) return;
            Progress.Value = pct;
            StatusText.Text = $"{_statusPrefix}… {pct:0}%";
        });
    }

    private List<string> BuildArgs(string ext, string output)
    {
        string S(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // -ss before -i: fast seek; with re-encoding it's still frame-accurate.
        bool copy = CopyBox.IsChecked == true;
        string hw = copy ? "cpu" : ChosenEncoder();

        var a = new List<string> { "-hide_banner", "-y" };
        // Let the GPU decode too when it's encoding; FFmpeg falls back to software decoding if it can't.
        if (hw != "cpu") a.AddRange(new[] { "-hwaccel", "auto" });
        a.AddRange(new[] { "-ss", S(_start), "-i", _source, "-t", S(_duration), "-map", "0:v:0", "-map", "0:a?" });

        if (copy)
        {
            a.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero" });
        }
        else
        {
            int height = Selected<int>(ResolutionBox);
            double fps = Selected<double>(FpsBox);
            string quality = Selected<string>(QualityBox);
            var (vcodec, acodec, usesCrf) = CodecsFor(ext, _info.VideoCodec);

            if (hw != "cpu")
            {
                // Swap the software encoder for the GPU one (keeping H.265 if the GPU supports it).
                bool hevc = vcodec == "libx265" && _gpuEncoders.Contains($"hevc_{hw}");
                vcodec = $"{(hevc ? "hevc" : "h264")}_{hw}";
            }
            bool gpu = hw != "cpu";

            // Intel/AMD encoders want NV12 input; NVENC and the CPU encoders take yuv420p.
            a.AddRange(new[] { "-c:v", vcodec, "-pix_fmt", hw is "qsv" or "amf" ? "nv12" : "yuv420p" });

            if (quality == "source" && _info.BitRate > 0)
            {
                // Match the source bitrate, scaled down if the resolution / frame rate are reduced.
                double factor = 1;
                if (height > 0 && _info.Height > 0) factor *= (double)height * height / ((double)_info.Height * _info.Height);
                if (fps > 0 && _info.Fps > 0) factor *= fps / _info.Fps;
                long br = Math.Max(200_000, (long)(_info.BitRate * factor));
                if (hw == "nvenc") a.AddRange(new[] { "-rc", "vbr" });
                if (hw == "amf") a.AddRange(new[] { "-rc", "vbr_peak" });
                a.AddRange(new[] { "-b:v", br.ToString(CultureInfo.InvariantCulture) });
                if (vcodec is "libx264" or "libx265" || gpu)
                    a.AddRange(new[] { "-maxrate", ((long)(br * 1.5)).ToString(CultureInfo.InvariantCulture),
                                       "-bufsize", (br * 2).ToString(CultureInfo.InvariantCulture) });
            }
            else if (gpu)
            {
                // Constant-quality modes of each GPU encoder (lower = better).
                string q = quality switch { "medium" => "24", "low" => "29", _ => "19" };
                switch (hw)
                {
                    case "nvenc": a.AddRange(new[] { "-rc", "vbr", "-cq", q, "-b:v", "0" }); break;
                    case "qsv": a.AddRange(new[] { "-global_quality", q }); break;
                    case "amf":
                        a.AddRange(new[] { "-rc", "cqp", "-qp_i", q, "-qp_p", q });
                        if (vcodec == "h264_amf") a.AddRange(new[] { "-qp_b", q });
                        break;
                }
            }
            else if (usesCrf)
            {
                int crf = (vcodec, quality) switch
                {
                    ("libvpx-vp9", "medium") => 33, ("libvpx-vp9", "low") => 40, ("libvpx-vp9", _) => 24,
                    ("libx265", "medium") => 26, ("libx265", "low") => 30, ("libx265", _) => 20,
                    (_, "medium") => 23, (_, "low") => 28, _ => 18,
                };
                a.AddRange(new[] { "-crf", crf.ToString(CultureInfo.InvariantCulture) });
                if (vcodec == "libvpx-vp9") a.AddRange(new[] { "-b:v", "0" });
            }
            else
            {
                a.AddRange(new[] { "-q:v", quality switch { "medium" => "5", "low" => "9", _ => "2" } });
            }

            if (vcodec is "libx264" or "libx265") a.AddRange(new[] { "-preset", "medium" });
            if (hw == "nvenc") a.AddRange(new[] { "-preset", "p5", "-tune", "hq" });
            if (hw == "qsv") a.AddRange(new[] { "-preset", "medium" });
            if (hw == "amf") a.AddRange(new[] { "-quality", "balanced" });
            if (vcodec == "libvpx-vp9") a.AddRange(new[] { "-row-mt", "1", "-deadline", "good", "-cpu-used", "4" });
            if (vcodec is "libx265" or "hevc_nvenc" or "hevc_qsv" or "hevc_amf" && ext is "mp4" or "mov" or "m4v") a.AddRange(new[] { "-tag:v", "hvc1" });

            if (height > 0) a.AddRange(new[] { "-vf", $"scale=-2:{height}:flags=lanczos" });
            if (fps > 0) a.AddRange(new[] { "-r", S(fps) });

            a.AddRange(new[] { "-c:a", acodec, "-b:a", acodec == "libopus" ? "160k" : "192k" });
        }

        if (ext is "mp4" or "mov" or "m4v") a.AddRange(new[] { "-movflags", "+faststart" });
        a.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
        return a;
    }

    /// <summary>Picks encoders that suit the container (keeping HEVC if the source is HEVC).</summary>
    private static (string video, string audio, bool usesCrf) CodecsFor(string ext, string sourceCodec) => ext switch
    {
        "webm" => ("libvpx-vp9", "libopus", true),
        "wmv" => ("wmv2", "wmav2", false),
        "mpg" => ("mpeg2video", "mp2", false),
        "avi" => ("libx264", "libmp3lame", true),
        "mp4" or "m4v" or "mov" or "mkv" when sourceCodec == "hevc" => ("libx265", "aac", true),
        _ => ("libx264", "aac", true),
    };

    // ---------- Cancel / close ----------

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_proc != null) KillExport();
        else Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_proc != null) KillExport();
    }

    private void KillExport()
    {
        _cancelled = true;
        StatusText.Text = "Cancelling…";
        try { _proc?.Kill(entireProcessTree: true); } catch { }
    }

    private static void TryDelete(string? path)
    {
        try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace VideoPlayer;

/// <summary>Properties of the source video, as reported by ffprobe (0 / "" when unknown).</summary>
public record VideoInfo(int Width, int Height, double Fps, string VideoCodec, long BitRate);

/// <summary>Finds ffmpeg.exe / ffprobe.exe and reads video properties.</summary>
public static class FFmpeg
{
    private static string ConfigFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterVideoPlayer", "ffmpeg_path.txt");

    /// <summary>Returns the path to ffmpeg.exe, asking the user to locate it if it can't be found.</summary>
    public static string? Locate(Window owner)
    {
        var found = Find();
        if (found != null) return found;

        var answer = MessageBox.Show(owner,
            "Exporting needs FFmpeg, which wasn't found on this PC.\n\n" +
            "You can install it by running this in a terminal, then restarting the player:\n" +
            "    winget install Gyan.FFmpeg\n\n" +
            "Or, if you already have ffmpeg.exe somewhere, click Yes to point to it.",
            "FFmpeg not found", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return null;

        var dlg = new OpenFileDialog { Title = "Locate ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|Programs|*.exe" };
        if (dlg.ShowDialog(owner) != true) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
            File.WriteAllText(ConfigFile, dlg.FileName);
        }
        catch { /* remembering the path is optional */ }
        return dlg.FileName;
    }

    /// <summary>Finds ffmpeg.exe without ever prompting the user.</summary>
    public static string? FindSilently() => Find();

    /// <summary>Codec names of the first video and first audio stream (e.g. "hevc", "dts"), or null if unknown.</summary>
    public static async Task<(string? video, string? audio)> ProbeCodecsAsync(string ffmpegPath, string file)
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
            if (!File.Exists(probe)) return (null, null);
            var psi = new ProcessStartInfo(probe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "json", file })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync();
            var json = await p.StandardOutput.ReadToEndAsync();
            await errTask;
            await p.WaitForExitAsync();

            using var doc = JsonDocument.Parse(json);
            string? video = null, audio = null;
            if (doc.RootElement.TryGetProperty("streams", out var streams))
                foreach (var s in streams.EnumerateArray())
                {
                    var type = Str(s, "codec_type");
                    var name = Str(s, "codec_name");
                    if (type == "video" && video == null && name is not ("mjpeg" or "png")) video = name; // skip cover art
                    else if (type == "audio" && audio == null) audio = name;
                }
            return (video, audio);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? Find()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var saved = File.ReadAllText(ConfigFile).Trim();
                if (File.Exists(saved)) return saved;
            }
        }
        catch { }

        var dirs = new List<string>
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin"),
        };
        // Read PATH fresh from the registry too, so a just-installed FFmpeg is found without a restart.
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", target) ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        dirs.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));

        foreach (var dir in dirs)
        {
            try
            {
                var exe = Path.Combine(Environment.ExpandEnvironmentVariables(dir), "ffmpeg.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }

        // winget's package folder (e.g. ...\WinGet\Packages\Gyan.FFmpeg_...\ffmpeg-7.1-full_build\bin\ffmpeg.exe)
        try
        {
            var pkgs = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pkgs))
                foreach (var pkg in Directory.GetDirectories(pkgs, "*FFmpeg*"))
                    foreach (var exe in Directory.GetFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories))
                        return exe;
        }
        catch { }

        return null;
    }

    /// <summary>Reads resolution, frame rate, codec and bitrate of the first video stream. Null if ffprobe is unavailable.</summary>
    public static async Task<VideoInfo?> ProbeAsync(string ffmpegPath, string file)
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
            if (!File.Exists(probe)) return null;

            var psi = new ProcessStartInfo(probe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-v", "error", "-select_streams", "v:0",
                         "-show_entries", "stream=codec_name,width,height,avg_frame_rate,r_frame_rate,bit_rate:format=bit_rate",
                         "-of", "json", file })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync();
            var json = await p.StandardOutput.ReadToEndAsync();
            await errTask;
            await p.WaitForExitAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) return null;
            var v = streams[0];

            int width = v.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            int height = v.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
            string codec = v.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
            double fps = ParseRate(Str(v, "avg_frame_rate"));
            if (fps <= 0) fps = ParseRate(Str(v, "r_frame_rate"));
            long bitrate = ParseLong(Str(v, "bit_rate"));
            if (bitrate <= 0 && root.TryGetProperty("format", out var fmt))
                bitrate = ParseLong(Str(fmt, "bit_rate")); // whole-file bitrate (includes audio) as a fallback

            return new VideoInfo(width, height, fps, codec, bitrate);
        }
        catch
        {
            return null;
        }
    }

    // ---------- GPU encoder detection ----------

    /// <summary>Hardware encoders we know how to drive. Only the ones that actually work on this PC are reported.</summary>
    private static readonly string[] GpuEncoderNames =
        { "h264_nvenc", "hevc_nvenc", "h264_qsv", "hevc_qsv", "h264_amf", "hevc_amf" };

    private static Task<HashSet<string>>? _gpuDetection;

    /// <summary>
    /// Returns the GPU encoders that work here (e.g. "h264_nvenc"). Each one is tried with a tiny test
    /// encode, because FFmpeg lists encoders it was built with even when the hardware isn't present.
    /// The result is cached for the rest of the session.
    /// </summary>
    public static Task<HashSet<string>> DetectGpuEncodersAsync(string ffmpegPath) =>
        _gpuDetection ??= DetectAsync(ffmpegPath);

    private static async Task<HashSet<string>> DetectAsync(string ffmpegPath)
    {
        var results = await Task.WhenAll(GpuEncoderNames.Select(async name =>
            (name, ok: await TryEncoderAsync(ffmpegPath, name))));
        return results.Where(r => r.ok).Select(r => r.name).ToHashSet();
    }

    private static async Task<bool> TryEncoderAsync(string ffmpegPath, string encoder)
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error",
                         "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30", "-frames:v", "5",
                         "-pix_fmt", "nv12", "-c:v", encoder, "-f", "null", "-" })
                psi.ArgumentList.Add(a);

            p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(cts.Token);
            await Task.WhenAll(outTask, errTask);
            return p.ExitCode == 0;
        }
        catch
        {
            try { p?.Kill(true); } catch { }
            return false;
        }
        finally
        {
            p?.Dispose();
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static long ParseLong(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ParseRate(string s)
    {
        var parts = s.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }
}

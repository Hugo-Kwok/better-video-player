using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using global::Windows.Graphics.DirectX;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Core;
using global::Windows.Media.Playback;
using global::Windows.Storage;
using WinVideoFrame = global::Windows.Media.VideoFrame;

namespace VideoPlayer;

public sealed class MediaFailedEventArgs : EventArgs
{
    public MediaFailedEventArgs(Exception error) => ErrorException = error;
    public Exception ErrorException { get; }
}

/// <summary>
/// Plays video with Windows' modern media engine (the one the Media Player app uses), so Microsoft Store
/// codec extensions such as AV1, HEVC and VP9 work. Each decoded frame is copied into a WPF bitmap shown
/// in an Image, so WPF controls can still be drawn on top of the video.
/// The API mirrors the parts of WPF's MediaElement this app used.
/// </summary>
public sealed class ModernPlayer : IDisposable
{
    private readonly MediaPlayer _player = new();
    private readonly Image _target;
    private readonly Dispatcher _dispatcher;

    private WinVideoFrame? _frame;           // GPU surface the engine copies each frame into
    private int _frameW, _frameH;
    private byte[] _pixels = Array.Empty<byte>();
    private WriteableBitmap? _bitmap;

    private int _busy;                       // 1 while a frame is being copied
    private volatile bool _pending;          // another frame arrived meanwhile
    private volatile bool _disposed;
    private bool _wantPlay;
    private int _openId;
    private MediaSource? _source;

    public event RoutedEventHandler? MediaOpened;
    public event RoutedEventHandler? MediaEnded;
    public event EventHandler<MediaFailedEventArgs>? MediaFailed;

    public ModernPlayer(Image target)
    {
        _target = target;
        _dispatcher = target.Dispatcher;

        _player.AutoPlay = false;
        _player.IsVideoFrameServerEnabled = true;   // we draw the frames ourselves
        _player.CommandManager.IsEnabled = false;   // no Windows media-key overlay integration
        _player.AudioCategory = MediaPlayerAudioCategory.Movie;

        _player.VideoFrameAvailable += (_, _) => _ = RenderFrameAsync();
        // When paused, show the frame at the new position after a seek.
        _player.PlaybackSession.SeekCompleted += (s, _) =>
        {
            if (s.PlaybackState != MediaPlaybackState.Playing) _ = RenderFrameAsync();
        };
        _player.MediaOpened += (_, _) => OnUi(() =>
        {
            MediaOpened?.Invoke(this, new RoutedEventArgs());
            _ = RenderFrameAsync(); // first frame, even if not playing
        });
        _player.MediaEnded += (_, _) => OnUi(() => MediaEnded?.Invoke(this, new RoutedEventArgs()));
        _player.MediaFailed += (_, e) =>
        {
            var message = !string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.ErrorMessage
                        : e.ExtendedErrorCode?.Message ?? e.Error.ToString();
            var error = new Exception($"{message} ({e.Error})", e.ExtendedErrorCode);
            OnUi(() => MediaFailed?.Invoke(this, new MediaFailedEventArgs(error)));
        };
    }

    // ---------- MediaElement-like API ----------

    public Uri? Source { get; private set; }

    public async void Open(string path)
    {
        int id = ++_openId;
        Source = new Uri(path);
        ClearPicture();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (id != _openId || _disposed) return; // another file was opened meanwhile
            var old = _source;
            _source = MediaSource.CreateFromStorageFile(file);
            _player.Source = _source;
            old?.Dispose();
            if (_wantPlay) _player.Play();
        }
        catch (Exception ex)
        {
            if (id == _openId) MediaFailed?.Invoke(this, new MediaFailedEventArgs(ex));
        }
    }

    /// <summary>Stops playback and releases the file so it can be moved or deleted.</summary>
    public void Close()
    {
        _openId++;
        _wantPlay = false;
        try { _player.Pause(); } catch { }
        _player.Source = null;
        _source?.Dispose();
        _source = null;
        Source = null;
        ClearPicture();
    }

    public void Play()
    {
        _wantPlay = true;
        if (_player.Source != null) _player.Play();
    }

    public void Pause()
    {
        _wantPlay = false;
        if (_player.Source != null) _player.Pause();
    }

    /// <summary>Pauses and moves exactly one frame forward or back, then shows that frame.</summary>
    public async Task StepFrameAsync(bool forward)
    {
        if (_player.Source == null) return;
        _wantPlay = false;
        if (forward) _player.StepForwardOneFrame();
        else _player.StepBackwardOneFrame();
        // Stepping doesn't always raise a frame event while paused, so draw the new frame ourselves.
        await Task.Delay(40);
        await RenderFrameAsync();
    }

    public TimeSpan Position
    {
        get => _player.PlaybackSession.Position;
        set => _player.PlaybackSession.Position = value;
    }

    public Duration NaturalDuration
    {
        get
        {
            var d = _player.PlaybackSession.NaturalDuration;
            return d > TimeSpan.Zero ? new Duration(d) : Duration.Automatic;
        }
    }

    /// <summary>Playback speed (1 = normal). Forward speeds only.</summary>
    public double PlaybackRate
    {
        get => _player.PlaybackSession.PlaybackRate;
        set => _player.PlaybackSession.PlaybackRate = value;
    }

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = value;
    }

    public int NaturalVideoWidth => (int)_player.PlaybackSession.NaturalVideoWidth;
    public int NaturalVideoHeight => (int)_player.PlaybackSession.NaturalVideoHeight;

    // ---------- Frame rendering ----------

    private void ClearPicture()
    {
        _target.Source = null;
        _bitmap = null;
    }

    private async Task RenderFrameAsync()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) { _pending = true; return; } // drop/queue while busy
        try
        {
            do
            {
                _pending = false;
                await CopyCurrentFrameAsync();
            } while (_pending && !_disposed);
        }
        catch
        {
            // A dropped frame is harmless; the next one will try again.
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task CopyCurrentFrameAsync()
    {
        var session = _player.PlaybackSession;
        int w = (int)session.NaturalVideoWidth, h = (int)session.NaturalVideoHeight;
        if (w <= 0 || h <= 0) return; // audio-only or not opened yet

        if (_frame == null || w != _frameW || h != _frameH)
        {
            _frame?.Dispose();
            _frame = WinVideoFrame.CreateAsDirect3D11SurfaceBacked(DirectXPixelFormat.B8G8R8A8UIntNormalized, w, h);
            _frameW = w;
            _frameH = h;
            _pixels = new byte[w * h * 4];
        }

        // GPU: engine -> our surface; then read it back as BGRA bytes.
        _player.CopyFrameToVideoSurface(_frame.Direct3DSurface);
        using var sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(_frame.Direct3DSurface, BitmapAlphaMode.Ignore);
        var pixels = _pixels;
        if (pixels.Length != sb.PixelWidth * sb.PixelHeight * 4) return; // size changed mid-copy
        sb.CopyToBuffer(pixels.AsBuffer());
        if (_disposed) return;

        // Hand the bytes to the WPF bitmap (UI thread). Invoke (not BeginInvoke) so the buffer
        // isn't overwritten by the next frame before it's been drawn.
        _dispatcher.Invoke(() =>
        {
            if (_disposed) return;
            if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
                _target.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        });
    }

    private void OnUi(Action action)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() => { if (!_disposed) action(); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _player.Dispose(); } catch { }
        try { _frame?.Dispose(); } catch { }
    }
}

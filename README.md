# Video Player (WPF, .NET 8)

Build & run (Windows, needs .NET 8 SDK: https://dotnet.microsoft.com/download):

    dotnet run

Single-file exe (always written to, and overwrites, publish\VideoPlayer.exe; close the player first):

    dotnet publish -c Release

Styled after Windows' Films & TV: black canvas, overlay controls that fade out while playing.

Controls: Open (Ctrl+O) / drag-drop a file / "Open with" this app (the file starts playing on launch), Rewind, Play/Pause, Fast forward, Full screen (F11, Esc to exit, or double-click anywhere on the video to enter/exit).
Clicking the video doesn't pause; hold it for 0.2 s to play forwards (right half) or backwards (left half) at the fast buttons' speed until you let go.
Scroll the mouse wheel over Rewind or Forward to change that button's skip amount independently (default 5s, 1s per notch, 1-600s).
Volume slider bottom-left (scroll over it, Up/Down keys, M = mute).
The Windows title bar is replaced by a translucent one that fades out with the controls; drag it to move, double-click to maximize.
The < and > buttons on the sides of the video (or Page Up / Page Down) play the previous / next video in the same folder, in File Explorer order.
The bin button (bottom right) moves the current video to the Recycle Bin after a confirmation, then opens the next video in the folder.
Hold the "<< x2" / "x2 >>" buttons (outside Rewind / Forward) to play backwards / forwards at that speed; scroll over them to change the speed (forwards 1.5x to 16x, backwards 0.25x to 16x).
Keys: Space = play/pause, Left/Right = skip back/forward, , / . = one frame back/forward (pauses).

## Trim & export

Click the scissors button (bottom right, or press T) to enter trim mode; the selection starts at the current position. The selected part of the timeline turns yellow;
drag the yellow handles, or use "Set start [" / "Set end ]" (keys [ / I and ] / O) at the current position.
Click Export… to save the selection as a new file. Format, resolution, quality and frame rate all default to the source;
"Fast copy" (ticked by default) skips re-encoding (instant, but cuts snap to keyframes).
The Encoder option uses your GPU (NVIDIA NVENC, Intel Quick Sync or AMD AMF) when one works on this PC, and falls back to
the CPU otherwise. GPU encoding applies to H.264/H.265 formats (MP4, MKV, MOV, M4V, AVI); WebM, WMV and MPG use the CPU.

Export uses FFmpeg. If it isn't found (next to the exe, on PATH, or via winget), the app asks you to install it
(`winget install Gyan.FFmpeg`) or point to an existing ffmpeg.exe; the location is remembered.

## Codecs

Playback uses Windows' modern media engine (the same one as the Media Player app, via Windows.Media.Playback),
so Microsoft Store codec extensions (AV1, HEVC, VP9, ...) are picked up. A file plays only if Windows has a decoder for it.
If a file can't play, the player shows its codecs (via ffprobe, if FFmpeg is installed) and a button for the matching
Microsoft Store extension: HEVC/H.265, VP9, AV1, MPEG-2, or Web Media (Theora/Vorbis). Install it and click "Try again".
DTS and Dolby TrueHD audio have no Store extension, and Dolby Digital (AC-3) isn't included on fresh Windows 11 24H2+ installs.

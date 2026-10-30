using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lemon.Book.Shell.Desktop.Narration;

/// <summary>
/// Plays clips with the Windows Media Control Interface (winmm), which decodes mp3 itself. Commands are sent from the
/// UI thread, whose message loop MCI needs. The fallback where Media Foundation is missing (Windows "N" editions without
/// the Media Feature Pack): it cannot change speed without changing the voice's pitch, so it always plays at 1×.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class MciNarrationAudio(string folder) : DesktopNarrationAudio(folder)
{
    private const string Alias = "narration";
    private bool _open;
    private bool _paused;
    private double _volume = 1;

    public override bool Play(string clip, double volume)
    {
        Stop();
        _volume = volume;
        if (!Send($"open \"{FullPath(clip)}\" type mpegvideo alias {Alias}"))
        {
            return false;
        }

        _open = true;
        Send($"set {Alias} time format milliseconds");
        ApplyVolume();
        if (!Send($"play {Alias}"))
        {
            Stop();
            return false;
        }

        return true;
    }

    public override void Pause()
    {
        if (_open && Send($"pause {Alias}"))
        {
            _paused = true;
        }
    }

    public override void Resume()
    {
        if (_open && Send($"resume {Alias}"))
        {
            _paused = false;
        }
    }

    public override void Stop()
    {
        if (_open)
        {
            Send($"close {Alias}");
        }

        _open = false;
        _paused = false;
    }

    public override double Volume
    {
        set
        {
            _volume = value;
            ApplyVolume();
        }
    }

    public override double Rate
    {
        set { }
    }

    public override double Position =>
        _open && double.TryParse(Query($"status {Alias} position"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            ? ms / 1000
            : 0;

    // MCI reports "stopped" once a clip has played to its end; a paused clip is "paused".
    public override bool IsEnded => !_open || (!_paused && Query($"status {Alias} mode") == "stopped");

    private void ApplyVolume()
    {
        if (_open)
        {
            Send($"setaudio {Alias} volume to {(int)Math.Round(Math.Clamp(_volume, 0, 1) * 1000)}");
        }
    }

    private static bool Send(string command)
    {
        var error = mciSendStringW(command, null, 0, IntPtr.Zero);
        if (error != 0)
        {
            Trace.TraceWarning($"MCI command failed ({error}): {command}");
        }

        return error == 0;
    }

    private static string Query(string command)
    {
        var buffer = new char[64];
        return mciSendStringW(command, buffer, buffer.Length, IntPtr.Zero) == 0
            ? new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length)
            : string.Empty;
    }

    [LibraryImport("winmm.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int mciSendStringW(string command, [Out] char[]? returnString, int returnLength, IntPtr callback);
}

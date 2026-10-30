using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lemon.Book.Shell.Desktop.Narration;

/// <summary>
/// Plays clips with AVFoundation's <c>AVAudioPlayer</c>, called through the Objective-C runtime. Its rate keeps the
/// voice's pitch (AVFoundation stretches time, not frequency).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacNarrationAudio : DesktopNarrationAudio
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    private static readonly IntPtr s_playerClass;
    private static readonly IntPtr s_stringClass;
    private static readonly IntPtr s_urlClass;

    private IntPtr _player;
    private bool _paused;
    private double _volume = 1;
    private double _rate = 1;

    static MacNarrationAudio()
    {
        // AVAudioPlayer's class is registered when its framework is loaded.
        NativeLibrary.TryLoad("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", out _);
        s_playerClass = objc_getClass("AVAudioPlayer");
        s_stringClass = objc_getClass("NSString");
        s_urlClass = objc_getClass("NSURL");
    }

    public MacNarrationAudio(string folder)
        : base(folder)
    {
    }

    public override bool Play(string clip, double volume)
    {
        Stop();
        _volume = volume;
        if (s_playerClass == IntPtr.Zero)
        {
            return false;
        }

        var path = SendString(s_stringClass, Selector("stringWithUTF8String:"), FullPath(clip));
        var url = Send(s_urlClass, Selector("fileURLWithPath:"), path);
        var player = Send(s_playerClass, Selector("alloc"));
        _player = Send(player, Selector("initWithContentsOfURL:error:"), url, IntPtr.Zero);
        if (_player == IntPtr.Zero)
        {
            Trace.TraceWarning($"AVAudioPlayer could not open {clip}.");
            return false;
        }

        SendFloat(_player, Selector("setVolume:"), (float)Math.Clamp(volume, 0, 1));

        // The rate only works if enabled before the player prepares; set before play, which prepares it.
        SendFlag(_player, Selector("setEnableRate:"), true);
        SendFloat(_player, Selector("setRate:"), (float)_rate);
        if (!SendBool(_player, Selector("play")))
        {
            Stop();
            return false;
        }

        return true;
    }

    public override void Pause()
    {
        if (_player != IntPtr.Zero)
        {
            Send(_player, Selector("pause"));
            _paused = true;
        }
    }

    public override void Resume()
    {
        if (_player != IntPtr.Zero)
        {
            SendBool(_player, Selector("play"));
            _paused = false;
        }
    }

    public override void Stop()
    {
        if (_player != IntPtr.Zero)
        {
            Send(_player, Selector("stop"));
            Send(_player, Selector("release"));
        }

        _player = IntPtr.Zero;
        _paused = false;
    }

    public override double Volume
    {
        set
        {
            _volume = value;
            if (_player != IntPtr.Zero)
            {
                SendFloat(_player, Selector("setVolume:"), (float)Math.Clamp(_volume, 0, 1));
            }
        }
    }

    public override double Rate
    {
        set
        {
            _rate = Math.Clamp(value, 0.5, 2);
            if (_player != IntPtr.Zero)
            {
                SendFloat(_player, Selector("setRate:"), (float)_rate);
            }
        }
    }

    // currentTime is in the clip's own seconds whatever the rate, as the sentence timings are.
    public override double Position => _player == IntPtr.Zero ? 0 : SendDouble(_player, Selector("currentTime"));

    // AVAudioPlayer stops by itself at the end of the clip; a paused clip is not playing either.
    public override bool IsEnded => _player == IntPtr.Zero || (!_paused && !SendBool(_player, Selector("isPlaying")));

    private static IntPtr Selector(string name) => sel_registerName(name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr SendString(IntPtr receiver, IntPtr selector, string argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendFloat(IntPtr receiver, IntPtr selector, float argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendFlag(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial double SendDouble(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SendBool(IntPtr receiver, IntPtr selector);
}

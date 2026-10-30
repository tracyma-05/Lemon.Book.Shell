using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace Lemon.Book.Shell.Desktop.Narration;

/// <summary>
/// Plays clips with Media Foundation's media engine (the one behind the HTML audio element in Windows): it decodes
/// mp3 itself, and it changes speed by stretching time, so the voice keeps its pitch (MCI, through DirectShow, would
/// raise it). One engine, audio only, plays every clip.
/// </summary>
/// <remarks>
/// The engine is called through its COM vtable with function pointers (the slots below are the SDK header's order,
/// mfmediaengine.h and mfobjects.h), which needs no interop assembly and stays trimmable for native AOT. Its event
/// callback is a source-generated COM class.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class MediaFoundationNarrationAudio : DesktopNarrationAudio
{
    private const uint MfVersion = 0x00020070;
    private const uint AudioOnly = 0x1;

    // Media engine events (MF_MEDIA_ENGINE_EVENT).
    private const uint EventError = 5;

    // IMFMediaEngine, after IUnknown's three slots.
    private const int SetSourceSlot = 3 + 3;
    private const int GetCurrentTimeSlot = 3 + 13;
    private const int SetDefaultPlaybackRateSlot = 3 + 19;
    private const int SetPlaybackRateSlot = 3 + 21;
    private const int IsEndedSlot = 3 + 24;
    private const int PlaySlot = 3 + 29;
    private const int PauseSlot = 3 + 30;
    private const int SetVolumeSlot = 3 + 34;

    // IMFAttributes::SetUnknown and IMFMediaEngineClassFactory::CreateInstance.
    private const int SetUnknownSlot = 3 + 24;
    private const int CreateInstanceSlot = 3;
    private const int ReleaseSlot = 2;

    private static readonly Guid ClassFactoryClsid = new("b44392da-499b-446b-a4cb-005fead0e6d5");
    private static readonly Guid ClassFactoryIid = new("4D645ACE-26AA-4688-9BE1-DF3516990B93");
    private static readonly Guid CallbackKey = new("c60381b8-83a4-41f8-a3d0-de05076849a9");

    private readonly IntPtr _engine;
    private readonly EngineEvents _events;
    private bool _open;
    private bool _paused;
    private int _clip;

    private MediaFoundationNarrationAudio(string folder, IntPtr engine, EngineEvents events)
        : base(folder)
    {
        _engine = engine;
        _events = events;
        events.Error += OnError;
    }

    /// <summary>Raised (on the UI thread) when a clip fails part-way.</summary>
    public override event EventHandler? Failed;

    /// <summary>The player, or null where Media Foundation is not installed or will not start.</summary>
    public static MediaFoundationNarrationAudio? TryCreate(string folder)
    {
        IntPtr factory = 0, attributes = 0, callback = 0;
        try
        {
            if (MFStartup(MfVersion, 0) < 0)
            {
                return null;
            }

            var clsid = ClassFactoryClsid;
            var iid = ClassFactoryIid;
            if (CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, &factory) < 0 || MFCreateAttributes(&attributes, 1) < 0)
            {
                return null;
            }

            var events = new EngineEvents();
            callback = (IntPtr)ComInterfaceMarshaller<IMediaEngineNotify>.ConvertToUnmanaged(events);
            var key = CallbackKey;
            if (Call(attributes, SetUnknownSlot, &key, callback) < 0)
            {
                return null;
            }

            IntPtr engine = 0;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr*, int>)Slot(factory, CreateInstanceSlot);
            if (create(factory, AudioOnly, attributes, &engine) < 0 || engine == 0)
            {
                return null;
            }

            return new MediaFoundationNarrationAudio(folder, engine, events);
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.TraceWarning($"Media Foundation unavailable for narration: {ex.Message}");
            return null;
        }
        finally
        {
            // The engine keeps its own references to the attributes' callback.
            Release(callback);
            Release(attributes);
            Release(factory);
        }
    }

    public override bool Play(string clip, double volume)
    {
        Stop();
        _clip++;
        var source = Marshal.StringToBSTR(FullPath(clip));
        try
        {
            if (Call(_engine, SetSourceSlot, source) < 0)
            {
                return false;
            }
        }
        finally
        {
            Marshal.FreeBSTR(source);
        }

        _open = true;
        Volume = volume;
        ApplyRate();

        // Play before the clip has loaded is allowed: it starts as soon as it can, as an audio element would.
        if (Call(_engine, PlaySlot) < 0)
        {
            Stop();
            return false;
        }

        return true;
    }

    public override void Pause()
    {
        if (_open && Call(_engine, PauseSlot) >= 0)
        {
            _paused = true;
        }
    }

    public override void Resume()
    {
        if (_open && Call(_engine, PlaySlot) >= 0)
        {
            _paused = false;
        }
    }

    public override void Stop()
    {
        if (_open)
        {
            Call(_engine, PauseSlot);
        }

        _open = false;
        _paused = false;
    }

    public override double Volume
    {
        set => CallDouble(_engine, SetVolumeSlot, Math.Clamp(value, 0, 1));
    }

    private double _rate = 1;

    public override double Rate
    {
        set
        {
            _rate = Math.Clamp(value, 0.5, 2);
            ApplyRate();
        }
    }

    // The engine's clock is in the clip's own seconds at any speed, as the sentence timings are.
    public override double Position =>
        _open ? ((delegate* unmanaged[Stdcall]<IntPtr, double>)Slot(_engine, GetCurrentTimeSlot))(_engine) : 0;

    public override bool IsEnded =>
        !_open || (!_paused && ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_engine, IsEndedSlot))(_engine) != 0);

    // Both rates: the default one carries over to the next clip, the current one changes the clip already playing.
    private void ApplyRate()
    {
        CallDouble(_engine, SetDefaultPlaybackRateSlot, _rate);
        CallDouble(_engine, SetPlaybackRateSlot, _rate);
    }

    private void OnError(object? sender, EventArgs e)
    {
        var clip = _clip;
        Dispatcher.UIThread.Post(() =>
        {
            if (_open && clip == _clip)
            {
                Trace.TraceWarning("Media Foundation could not play a narration clip.");
                Stop();
                Failed?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private static IntPtr Slot(IntPtr com, int slot) => (*(IntPtr**)com)[slot];

    private static int Call(IntPtr com, int slot) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(com, slot))(com);

    private static int Call(IntPtr com, int slot, IntPtr argument) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(com, slot))(com, argument);

    private static int Call(IntPtr com, int slot, Guid* key, IntPtr argument) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)Slot(com, slot))(com, key, argument);

    private static int CallDouble(IntPtr com, int slot, double argument) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, double, int>)Slot(com, slot))(com, argument);

    private static void Release(IntPtr com)
    {
        if (com != 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, ReleaseSlot))(com);
        }
    }

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateAttributes(IntPtr* attributes, uint initialSize);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* instance);

    /// <summary>IMFMediaEngineNotify: the engine's events, on a Media Foundation thread.</summary>
    [GeneratedComInterface]
    [Guid("fee7c112-e776-42b5-9bbf-0048524e2bd5")]
    internal partial interface IMediaEngineNotify
    {
        [PreserveSig]
        int EventNotify(uint eventCode, nuint param1, uint param2);
    }

    [GeneratedComClass]
    internal sealed partial class EngineEvents : IMediaEngineNotify
    {
        public event EventHandler? Error;

        public int EventNotify(uint eventCode, nuint param1, uint param2)
        {
            if (eventCode == EventError)
            {
                Error?.Invoke(this, EventArgs.Empty);
            }

            return 0;
        }
    }
}

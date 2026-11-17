using System.Reflection;
using System.Runtime.InteropServices;

namespace Rta.App.Ndi;

/// <summary>
/// Minimal P/Invoke layer over the NDI runtime (Processing.NDI.Lib.x64.dll, NDI 5/6). The DLL is not
/// shipped with the app: it is found through NDI_RUNTIME_DIR_V6 / V5 (set by NDI Tools or the NDI
/// Runtime installer). Struct layouts follow the NDI SDK headers (x64, natural alignment).
/// NDI® is a registered trademark of Vizrt NDI AB.
/// </summary>
internal static unsafe partial class NdiNative
{
    public const string Lib = "Processing.NDI.Lib.x64";

    static NdiNative() => NativeLibrary.SetDllImportResolver(typeof(NdiNative).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        foreach (var dir in RuntimeDirs())
        {
            var file = Path.Combine(dir, Lib + ".dll");
            if (File.Exists(file) && NativeLibrary.TryLoad(file, out var h)) return h;
        }
        return NativeLibrary.TryLoad(Lib + ".dll", asm, path, out var h2) ? h2 : IntPtr.Zero;
    }

    public static IEnumerable<string> RuntimeDirs()
    {
        foreach (var v in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } d) yield return d;
        yield return @"C:\Program Files\NDI\NDI 6 Runtime\v6";
        yield return @"C:\Program Files\NDI\NDI 6 Tools\Runtime";
        yield return @"C:\Program Files\NDI\NDI 5 Runtime\v5";
        yield return AppContext.BaseDirectory;
    }

    // ---------- structs (NDI SDK v5/v6) ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public IntPtr p_ndi_name;
        public IntPtr p_groups;
        [MarshalAs(UnmanagedType.U1)] public bool clock_video;
        [MarshalAs(UnmanagedType.U1)] public bool clock_audio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int xres, yres;
        public int FourCC;
        public int frame_rate_N, frame_rate_D;
        public float picture_aspect_ratio;
        public int frame_format_type;
        public long timecode;
        public IntPtr p_data;
        public int line_stride_in_bytes;
        public IntPtr p_metadata;
        public long timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrameV2
    {
        public int sample_rate, no_channels, no_samples;
        public long timecode;
        public IntPtr p_data;
        public int channel_stride_in_bytes;
        public IntPtr p_metadata;
        public long timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MetadataFrame
    {
        public int length;
        public long timecode;
        public IntPtr p_data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Source
    {
        public IntPtr p_ndi_name;
        public IntPtr p_url_address;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FindCreate
    {
        [MarshalAs(UnmanagedType.U1)] public bool show_local_sources;
        public IntPtr p_groups;
        public IntPtr p_extra_ips;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RecvCreateV3
    {
        public Source source_to_connect_to;
        public int color_format;
        public int bandwidth;
        [MarshalAs(UnmanagedType.U1)] public bool allow_video_fields;
        public IntPtr p_ndi_recv_name;
    }

    public const int FourCC_BGRA = 'B' | ('G' << 8) | ('R' << 16) | ('A' << 24);
    public const int FormatProgressive = 1;
    public const long TimecodeSynthesize = long.MaxValue;
    public const int FrameNone = 0, FrameVideo = 1, FrameAudio = 2, FrameMetadata = 3;

    // ---------- functions ----------

    [LibraryImport(Lib, EntryPoint = "NDIlib_initialize")] [return: MarshalAs(UnmanagedType.U1)] public static partial bool Initialize();
    [LibraryImport(Lib, EntryPoint = "NDIlib_version")] public static partial IntPtr Version();
    [LibraryImport(Lib, EntryPoint = "NDIlib_is_supported_CPU")] [return: MarshalAs(UnmanagedType.U1)] public static partial bool IsSupportedCpu();

    [LibraryImport(Lib, EntryPoint = "NDIlib_send_create")] public static partial IntPtr SendCreateFn(SendCreate* settings);
    [LibraryImport(Lib, EntryPoint = "NDIlib_send_destroy")] public static partial void SendDestroy(IntPtr send);
    [LibraryImport(Lib, EntryPoint = "NDIlib_send_send_video_v2")] public static partial void SendVideo(IntPtr send, VideoFrameV2* frame);
    [LibraryImport(Lib, EntryPoint = "NDIlib_send_send_audio_v2")] public static partial void SendAudio(IntPtr send, AudioFrameV2* frame);
    [LibraryImport(Lib, EntryPoint = "NDIlib_send_send_metadata")] public static partial void SendMetadata(IntPtr send, MetadataFrame* frame);
    [LibraryImport(Lib, EntryPoint = "NDIlib_send_get_no_connections")] public static partial int SendConnections(IntPtr send, uint timeoutMs);

    [LibraryImport(Lib, EntryPoint = "NDIlib_find_create_v2")] public static partial IntPtr FindCreateFn(FindCreate* settings);
    [LibraryImport(Lib, EntryPoint = "NDIlib_find_destroy")] public static partial void FindDestroy(IntPtr find);
    [LibraryImport(Lib, EntryPoint = "NDIlib_find_wait_for_sources")] [return: MarshalAs(UnmanagedType.U1)] public static partial bool FindWait(IntPtr find, uint timeoutMs);
    [LibraryImport(Lib, EntryPoint = "NDIlib_find_get_current_sources")] public static partial Source* FindSources(IntPtr find, uint* count);

    [LibraryImport(Lib, EntryPoint = "NDIlib_recv_create_v3")] public static partial IntPtr RecvCreate(RecvCreateV3* settings);
    [LibraryImport(Lib, EntryPoint = "NDIlib_recv_destroy")] public static partial void RecvDestroy(IntPtr recv);
    [LibraryImport(Lib, EntryPoint = "NDIlib_recv_capture_v2")] public static partial int RecvCapture(IntPtr recv, VideoFrameV2* video, AudioFrameV2* audio, MetadataFrame* meta, uint timeoutMs);
    [LibraryImport(Lib, EntryPoint = "NDIlib_recv_free_video_v2")] public static partial void RecvFreeVideo(IntPtr recv, VideoFrameV2* video);
    [LibraryImport(Lib, EntryPoint = "NDIlib_recv_free_audio_v2")] public static partial void RecvFreeAudio(IntPtr recv, AudioFrameV2* audio);
    [LibraryImport(Lib, EntryPoint = "NDIlib_recv_free_metadata")] public static partial void RecvFreeMetadata(IntPtr recv, MetadataFrame* meta);
}

/// <summary>Loads and initialises the NDI runtime once; reports why if it can't.</summary>
public static class NdiRuntime
{
    private static readonly Lazy<(bool Ok, string Message)> State = new(Load);

    public static bool Available => State.Value.Ok;
    public static string Message => State.Value.Message;

    private static (bool, string) Load()
    {
        try
        {
            if (!NdiNative.IsSupportedCpu()) return (false, "This CPU is not supported by NDI.");
            if (!NdiNative.Initialize()) return (false, "The NDI runtime could not be initialised.");
            return (true, Marshal.PtrToStringUTF8(NdiNative.Version()) ?? "NDI");
        }
        catch (DllNotFoundException)
        {
            return (false, "NDI runtime not found. Install NDI Tools or the NDI Runtime from ndi.video, then restart the app.");
        }
        catch (EntryPointNotFoundException ex)
        {
            return (false, $"The installed NDI runtime is too old ({ex.Message}). Install NDI 5 or 6.");
        }
    }

    public static void EnsureAvailable()
    {
        if (!Available) throw new InvalidOperationException(Message);
    }
}

/// <summary>One NDI source this app publishes (video and/or audio and metadata).</summary>
public sealed unsafe class NdiSender : IDisposable
{
    private IntPtr _send;
    private readonly object _lock = new();
    public string Name { get; }

    public NdiSender(string name)
    {
        NdiRuntime.EnsureAvailable();
        Name = name;
        var namePtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var settings = new NdiNative.SendCreate { p_ndi_name = namePtr, clock_video = false, clock_audio = false };
            _send = NdiNative.SendCreateFn(&settings);
        }
        finally { Marshal.FreeCoTaskMem(namePtr); }
        if (_send == IntPtr.Zero) throw new InvalidOperationException($"NDI could not create the source '{name}'.");
    }

    /// <summary>Sends one BGRA frame (premultiplied alpha is fine; we draw opaque).</summary>
    public void SendBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride, int fpsN, int fpsD = 1)
    {
        lock (_lock)
        {
            if (_send == IntPtr.Zero) return;
            fixed (byte* p = pixels)
            {
                var f = new NdiNative.VideoFrameV2
                {
                    xres = width, yres = height, FourCC = NdiNative.FourCC_BGRA,
                    frame_rate_N = fpsN, frame_rate_D = fpsD, picture_aspect_ratio = width / (float)height,
                    frame_format_type = NdiNative.FormatProgressive, timecode = NdiNative.TimecodeSynthesize,
                    p_data = (IntPtr)p, line_stride_in_bytes = stride,
                };
                NdiNative.SendVideo(_send, &f); // synchronous: NDI copies/compresses before returning
            }
        }
    }

    /// <summary>Sends planar 32-bit float audio (one contiguous block per channel).</summary>
    public void SendAudio(ReadOnlySpan<float> planar, int channels, int samplesPerChannel, int sampleRate)
    {
        lock (_lock)
        {
            if (_send == IntPtr.Zero) return;
            fixed (float* p = planar)
            {
                var f = new NdiNative.AudioFrameV2
                {
                    sample_rate = sampleRate, no_channels = channels, no_samples = samplesPerChannel,
                    timecode = NdiNative.TimecodeSynthesize, p_data = (IntPtr)p, channel_stride_in_bytes = samplesPerChannel * 4,
                };
                NdiNative.SendAudio(_send, &f);
            }
        }
    }

    public void SendMetadata(string xml)
    {
        lock (_lock)
        {
            if (_send == IntPtr.Zero) return;
            var ptr = Marshal.StringToCoTaskMemUTF8(xml);
            try
            {
                var f = new NdiNative.MetadataFrame { length = 0, timecode = NdiNative.TimecodeSynthesize, p_data = ptr };
                NdiNative.SendMetadata(_send, &f);
            }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
    }

    /// <summary>Number of receivers currently connected.</summary>
    public int Connections
    {
        get { lock (_lock) return _send == IntPtr.Zero ? 0 : NdiNative.SendConnections(_send, 0); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_send != IntPtr.Zero) NdiNative.SendDestroy(_send);
            _send = IntPtr.Zero;
        }
    }
}

public sealed record NdiSourceInfo(string Name, string? Url);

public sealed record NdiProbeResult(string Source, bool Connected, int? Width, int? Height, string? FourCC, int? AudioChannels, int? AudioRate, string? Metadata, string Message);

/// <summary>Finds NDI sources on the network and can briefly receive one (a self-check).</summary>
public static unsafe class NdiTools
{
    public static IReadOnlyList<NdiSourceInfo> FindSources(int waitMs = 2500)
    {
        NdiRuntime.EnsureAvailable();
        var fc = new NdiNative.FindCreate { show_local_sources = true };
        var find = NdiNative.FindCreateFn(&fc);
        if (find == IntPtr.Zero) return [];
        try
        {
            var deadline = Environment.TickCount64 + waitMs;
            while (Environment.TickCount64 < deadline) NdiNative.FindWait(find, (uint)Math.Max(1, deadline - Environment.TickCount64));
            uint n = 0;
            var src = NdiNative.FindSources(find, &n);
            var list = new List<NdiSourceInfo>();
            for (var i = 0; i < n; i++)
                list.Add(new NdiSourceInfo(Marshal.PtrToStringUTF8(src[i].p_ndi_name) ?? "", Marshal.PtrToStringUTF8(src[i].p_url_address)));
            return list.OrderBy(s => s.Name).ToList();
        }
        finally { NdiNative.FindDestroy(find); }
    }

    /// <summary>Connects to a source and reports the first video, audio and metadata frames seen within the timeout.</summary>
    public static NdiProbeResult Probe(string sourceName, int timeoutMs = 5000)
    {
        NdiRuntime.EnsureAvailable();
        var namePtr = Marshal.StringToCoTaskMemUTF8(sourceName);
        var recvName = Marshal.StringToCoTaskMemUTF8("RTA self-check");
        try
        {
            var rc = new NdiNative.RecvCreateV3
            {
                source_to_connect_to = new NdiNative.Source { p_ndi_name = namePtr },
                color_format = 0, bandwidth = 100, allow_video_fields = false, p_ndi_recv_name = recvName,
            };
            var recv = NdiNative.RecvCreate(&rc);
            if (recv == IntPtr.Zero) return new NdiProbeResult(sourceName, false, null, null, null, null, null, null, "Could not create a receiver.");
            try
            {
                int? w = null, h = null, ch = null, rate = null;
                string? fourcc = null, meta = null;
                var deadline = Environment.TickCount64 + timeoutMs;
                while (Environment.TickCount64 < deadline && (w is null || meta is null))
                {
                    NdiNative.VideoFrameV2 v; NdiNative.AudioFrameV2 a; NdiNative.MetadataFrame m;
                    switch (NdiNative.RecvCapture(recv, &v, &a, &m, 200))
                    {
                        case NdiNative.FrameVideo:
                            w = v.xres; h = v.yres;
                            fourcc = new string([(char)(v.FourCC & 0xFF), (char)((v.FourCC >> 8) & 0xFF), (char)((v.FourCC >> 16) & 0xFF), (char)((v.FourCC >> 24) & 0xFF)]);
                            NdiNative.RecvFreeVideo(recv, &v);
                            break;
                        case NdiNative.FrameAudio:
                            ch = a.no_channels; rate = a.sample_rate;
                            NdiNative.RecvFreeAudio(recv, &a);
                            if (w is null && meta is null && Environment.TickCount64 > deadline - timeoutMs / 2) deadline = Environment.TickCount64; // audio-only source
                            break;
                        case NdiNative.FrameMetadata:
                            meta ??= Marshal.PtrToStringUTF8(m.p_data);
                            NdiNative.RecvFreeMetadata(recv, &m);
                            break;
                    }
                }
                var ok = w is not null || ch is not null;
                return new NdiProbeResult(sourceName, ok, w, h, fourcc, ch, rate, meta is { Length: > 300 } ? meta[..300] + "…" : meta,
                    ok ? "Received frames." : "Connected but no frames arrived (is the source sending?).");
            }
            finally { NdiNative.RecvDestroy(recv); }
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
            Marshal.FreeCoTaskMem(recvName);
        }
    }
}

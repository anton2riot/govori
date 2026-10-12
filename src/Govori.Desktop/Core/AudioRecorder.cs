using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace Govori;

public sealed record Microphone(int Id, string Name);
public sealed record Recording(string Path, double Duration, float Peak);

public sealed class AudioRecorder : IDisposable
{
    internal const int SpeechSampleRate = 16000;
    private nint stream;
    private Task<Recording>? capture;
    private Task<Recording?>? stopTask;
    private BlockingCollection<short[]>? buffers;
    private readonly Pa.Callback callback;
    private volatile Exception? callbackError;
    private volatile bool stopping, ready;
    private float level;
    private bool initialized;
    private readonly object streamGate = new();
    public bool IsRecording => !stopping && callbackError == null && capture is { IsCompleted: false };
    public bool IsReady => ready;
    internal Exception? Error => callbackError ?? capture?.Exception?.GetBaseException();
    internal event Action? BecameReady;
    public float CurrentLevel => level;

    public AudioRecorder() { callback = OnAudio; Check(Pa.Initialize()); initialized = true; }

    public List<Microphone> Devices()
    {
        var result = new List<Microphone> { new(-1, Localization.T("DefaultMicrophone")) };
        int count = Pa.GetDeviceCount(); Check(count < 0 ? count : 0);
        for (int i = 0; i < count; i++)
        {
            var info = Marshal.PtrToStructure<Pa.DeviceInfo>(Pa.GetDeviceInfo(i));
            if (info.MaxInputChannels > 0) result.Add(new(i, Marshal.PtrToStringUTF8(info.Name) ?? Localization.T("MicrophoneName", i)));
        }
        return result;
    }

    public void Start(int device)
    {
        if (IsRecording) throw new InvalidOperationException(Localization.T("AlreadyRecording"));
        if (device < 0) device = Pa.GetDefaultInputDevice();
        if (device < 0) throw new InvalidOperationException(Localization.T("MicrophoneMissing"));
        var pointer = Pa.GetDeviceInfo(device);
        if (pointer == 0) throw new InvalidOperationException(Localization.T("MicrophoneUnavailable"));
        var info = Marshal.PtrToStructure<Pa.DeviceInfo>(pointer);
        if (info.MaxInputChannels < 1) throw new InvalidOperationException(Localization.T("DeviceCannotRecord"));
        int sampleRate = (int)info.DefaultSampleRate;
        var input = new Pa.StreamParameters { Device = device, Channels = 1, Format = 8, Latency = info.DefaultLowInputLatency };
        // Match the original speech recorder's upload size. Some native audio
        // backends require the device rate, so retain that working fallback.
        if (Pa.IsFormatSupported(ref input, 0, SpeechSampleRate) == 0) sampleRate = SpeechSampleRate;
        stopping = false; ready = false; level = 0; callbackError = null; stopTask = null;
        buffers = new BlockingCollection<short[]>(256);
        string path = Path.Combine(Configuration.AudioDirectory, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            Check(Pa.OpenStream(out stream, ref input, 0, sampleRate, 256, 0, Marshal.GetFunctionPointerForDelegate(callback), 0));
            // The consumer blocks while waiting for audio. Give it a dedicated
            // thread so it cannot starve UI timers or other thread-pool work.
            var queue = buffers;
            capture = Task.Factory.StartNew(() => Capture(path, sampleRate, queue), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Check(Pa.StartStream(stream));
        }
        catch
        {
            if (stream != 0) { Pa.CloseStream(stream); stream = 0; }
            buffers.CompleteAdding();
            if (capture != null) { try { capture.GetAwaiter().GetResult(); } catch { } }
            buffers.Dispose(); buffers = null; capture = null;
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    private int OnAudio(nint input, nint output, nuint frames, nint time, nuint flags, nint user)
    {
        if (stopping || callbackError != null) return 2;
        if (input == 0) return 0;
        try
        {
            var buffer = new short[checked((int)frames)]; Marshal.Copy(input, buffer, 0, buffer.Length);
            if (!buffers!.TryAdd(buffer)) throw new IOException(Localization.T("AudioQueueFull"));
            // Silence also confirms that capture is live. Notify the UI on the
            // first buffer rather than making it wait for a polling timer.
            if (!ready && buffer.Length > 0) { ready = true; BecameReady?.Invoke(); }
            return 0;
        }
        catch (Exception ex) { callbackError = ex; return 2; }
    }

    private Recording Capture(string path, int sampleRate, BlockingCollection<short[]> queue)
    {
        long bytes = 0; float peak = 0;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            using var writer = new BinaryWriter(file);
            WriteHeader(writer, sampleRate, 0);
            foreach (var buffer in queue.GetConsumingEnumerable())
            {
                float current = 0;
                foreach (var sample in buffer) { writer.Write(sample); current = Math.Max(current, Math.Abs(sample / 32768f)); }
                bytes += buffer.Length * 2; level = current; peak = Math.Max(peak, current);
            }
            file.Position = 0; WriteHeader(writer, sampleRate, checked((int)bytes));
            if (callbackError != null) throw new InvalidOperationException(Localization.T("RecordingError"), callbackError);
            return new(path, bytes / (sampleRate * 2.0), peak);
        }
        finally { ready = false; level = 0; }
    }

    internal static void WriteHeader(BinaryWriter writer, int sampleRate, int bytes)
    {
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
        writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes);
    }

    public Task<Recording?> StopAsync() => stopTask ??= StopCoreAsync();

    private async Task<Recording?> StopCoreAsync()
    {
        if (capture == null) return null;
        stopping = true;
        ready = false;
        await Task.Run(() =>
        {
            lock (streamGate)
            {
                if (stream != 0) { Pa.AbortStream(stream); Pa.CloseStream(stream); stream = 0; }
            }
            buffers!.CompleteAdding();
        }).ConfigureAwait(false);
        try { return await capture.ConfigureAwait(false); }
        finally { buffers?.Dispose(); buffers = null; }
    }

    private static void Check(int error)
    {
        if (error < 0) throw new InvalidOperationException(Localization.T("MicrophoneError", Marshal.PtrToStringUTF8(Pa.GetErrorText(error)) ?? ""));
    }

    public void Dispose()
    {
        if (!initialized) return;
        try { StopAsync().GetAwaiter().GetResult(); }
        finally { Pa.Terminate(); initialized = false; }
    }

    internal static class Pa
    {
        private const string Lib = "govori-portaudio";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Callback(nint input, nint output, nuint frames, nint time, nuint flags, nint user);
        [StructLayout(LayoutKind.Sequential)] internal struct StreamParameters { public int Device, Channels; public nuint Format; public double Latency; public nint HostInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo { public int Version; public nint Name; public int HostApi, MaxInputChannels, MaxOutputChannels; public double DefaultLowInputLatency, DefaultLowOutputLatency, DefaultHighInputLatency, DefaultHighOutputLatency, DefaultSampleRate; }
        [DllImport(Lib, EntryPoint="Pa_Initialize", CallingConvention=CallingConvention.Cdecl)] internal static extern int Initialize();
        [DllImport(Lib, EntryPoint="Pa_Terminate", CallingConvention=CallingConvention.Cdecl)] internal static extern int Terminate();
        [DllImport(Lib, EntryPoint="Pa_GetDeviceCount", CallingConvention=CallingConvention.Cdecl)] internal static extern int GetDeviceCount();
        [DllImport(Lib, EntryPoint="Pa_GetDefaultInputDevice", CallingConvention=CallingConvention.Cdecl)] internal static extern int GetDefaultInputDevice();
        [DllImport(Lib, EntryPoint="Pa_GetDeviceInfo", CallingConvention=CallingConvention.Cdecl)] internal static extern nint GetDeviceInfo(int device);
        [DllImport(Lib, EntryPoint="Pa_GetErrorText", CallingConvention=CallingConvention.Cdecl)] internal static extern nint GetErrorText(int error);
        [DllImport(Lib, EntryPoint="Pa_IsFormatSupported", CallingConvention=CallingConvention.Cdecl)] internal static extern int IsFormatSupported(ref StreamParameters input, nint output, double sampleRate);
        [DllImport(Lib, EntryPoint="Pa_OpenStream", CallingConvention=CallingConvention.Cdecl)] internal static extern int OpenStream(out nint stream, ref StreamParameters input, nint output, double sampleRate, nuint frames, nuint flags, nint callback, nint user);
        [DllImport(Lib, EntryPoint="Pa_StartStream", CallingConvention=CallingConvention.Cdecl)] internal static extern int StartStream(nint stream);
        [DllImport(Lib, EntryPoint="Pa_AbortStream", CallingConvention=CallingConvention.Cdecl)] internal static extern int AbortStream(nint stream);
        [DllImport(Lib, EntryPoint="Pa_CloseStream", CallingConvention=CallingConvention.Cdecl)] internal static extern int CloseStream(nint stream);
    }
}

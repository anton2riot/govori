using System.Runtime.InteropServices;

namespace Govori;

internal static class ReadySound
{
    internal const double DurationSeconds = 0.1;

    internal static Task<bool> PlayAsync() => Task.Run(Play);

    private static bool Play()
    {
        nint stream = 0;
        bool initialized = false;
        try
        {
            if (Pa.Initialize() < 0) return false;
            initialized = true;
            int device = Pa.GetDefaultOutputDevice();
            if (device < 0) return false;
            nint pointer = Pa.GetDeviceInfo(device);
            if (pointer == 0) return false;
            var info = Marshal.PtrToStructure<AudioRecorder.Pa.DeviceInfo>(pointer);
            if (info.MaxOutputChannels < 1) return false;
            int sampleRate = (int)info.DefaultSampleRate;
            var output = new AudioRecorder.Pa.StreamParameters { Device = device, Channels = 1, Format = 8, Latency = info.DefaultLowOutputLatency };
            if (Pa.OpenStream(out stream, 0, ref output, sampleRate, 256, 0, 0, 0) < 0) return false;
            if (Pa.StartStream(stream) < 0) return false;
            short[] samples = CreateSamples(sampleRate);
            if (Pa.WriteStream(stream, samples, (nuint)samples.Length) < 0) return false;
            return Pa.StopStream(stream) >= 0;
        }
        catch { return false; }
        finally
        {
            if (stream != 0) Pa.CloseStream(stream);
            if (initialized) Pa.Terminate();
        }
    }

    internal static short[] CreateSamples(int sampleRate)
    {
        var samples = new short[(int)(sampleRate * DurationSeconds)];
        for (int i = 0; i < samples.Length; i++)
        {
            // A smooth envelope prevents clicks at the start and end.
            double envelope = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (samples.Length - 1));
            samples[i] = (short)(short.MaxValue * 0.12 * envelope * Math.Sin(2 * Math.PI * 1046.5 * i / sampleRate));
        }
        return samples;
    }

    private static class Pa
    {
        private const string Lib = "govori-portaudio";
        [DllImport(Lib, EntryPoint = "Pa_Initialize", CallingConvention = CallingConvention.Cdecl)] internal static extern int Initialize();
        [DllImport(Lib, EntryPoint = "Pa_Terminate", CallingConvention = CallingConvention.Cdecl)] internal static extern int Terminate();
        [DllImport(Lib, EntryPoint = "Pa_GetDefaultOutputDevice", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetDefaultOutputDevice();
        [DllImport(Lib, EntryPoint = "Pa_GetDeviceInfo", CallingConvention = CallingConvention.Cdecl)] internal static extern nint GetDeviceInfo(int device);
        [DllImport(Lib, EntryPoint = "Pa_OpenStream", CallingConvention = CallingConvention.Cdecl)] internal static extern int OpenStream(out nint stream, nint input, ref AudioRecorder.Pa.StreamParameters output, double sampleRate, nuint frames, nuint flags, nint callback, nint user);
        [DllImport(Lib, EntryPoint = "Pa_StartStream", CallingConvention = CallingConvention.Cdecl)] internal static extern int StartStream(nint stream);
        [DllImport(Lib, EntryPoint = "Pa_WriteStream", CallingConvention = CallingConvention.Cdecl)] internal static extern int WriteStream(nint stream, short[] samples, nuint frames);
        [DllImport(Lib, EntryPoint = "Pa_StopStream", CallingConvention = CallingConvention.Cdecl)] internal static extern int StopStream(nint stream);
        [DllImport(Lib, EntryPoint = "Pa_CloseStream", CallingConvention = CallingConvention.Cdecl)] internal static extern int CloseStream(nint stream);
    }
}

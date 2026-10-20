using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Govori;

internal sealed class SingleInstance : IDisposable
{
    private readonly FileStream lease;
    private readonly NamedPipeServerStream server;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task listener;

    private SingleInstance(FileStream lease, string directory, Action activate)
    {
        this.lease = lease;
        server = new NamedPipeServerStream(PipeName(directory), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        listener = Task.Run(() => ListenAsync(activate));
    }

    internal static SingleInstance? TryAcquire(string directory, Action activate)
    {
        Directory.CreateDirectory(directory);
        FileStream lease;
        try { lease = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        try { return new SingleInstance(lease, directory, activate); }
        catch { lease.Dispose(); throw; }
    }

    private static string PipeName(string directory)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return "Govori-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
    }

    internal static async Task NotifyAsync(string directory, bool activate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        var process = new byte[4];
        await client.ReadExactlyAsync(process, timeout.Token).ConfigureAwait(false);
        if (activate && OperatingSystem.IsWindows()) WindowsNative.AllowSetForegroundWindow(BitConverter.ToUInt32(process));
        await client.WriteAsync(new byte[] { activate ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
        await client.ReadExactlyAsync(new byte[1], timeout.Token).ConfigureAwait(false);
    }

    private async Task ListenAsync(Action activate)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(shutdown.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await server.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), timeout.Token);
                var command = new byte[1];
                await server.ReadExactlyAsync(command, timeout.Token);
                if (command[0] == 1) activate();
                await server.WriteAsync(new byte[] { 1 }, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { if (server.IsConnected) server.Disconnect(); }
        }
    }

    public void Dispose()
    {
        shutdown.Cancel();
        listener.GetAwaiter().GetResult();
        server.Dispose(); shutdown.Dispose(); lease.Dispose();
    }
}

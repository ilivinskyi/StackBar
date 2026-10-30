using System.IO;
using System.IO.Pipes;
using System.Text;

namespace TaskbarGrouper.Services;

public sealed class IpcService : IDisposable
{
    private const string PipeName = "StackBar_InstantFlyout_Pipe";
    private readonly Action<string, int, int> _onOpenGroupRequested;
    private readonly CancellationTokenSource _cts = new();

    public IpcService(Action<string, int, int> onOpenGroupRequested)
    {
        _onOpenGroupRequested = onOpenGroupRequested;
        Task.Run(ListenLoopAsync);
    }

    /// <summary>
    /// Attempts to signal an already-running warm StackBar background instance to open a group immediately.
    /// Returns true if the warm instance handled it (<15ms).
    /// </summary>
    public static bool TrySignalWarmInstance(string groupId, int cursorX, int cursorY)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 60);
            string payload = $"{groupId}|{cursorX}|{cursorY}\n";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(_cts.Token);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var parts = line.Split('|');
                    string groupId = parts[0];
                    int x = parts.Length > 1 && int.TryParse(parts[1], out int px) ? px : -1;
                    int y = parts.Length > 2 && int.TryParse(parts[2], out int py) ? py : -1;

                    _onOpenGroupRequested(groupId, x, y);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                if (_cts.IsCancellationRequested)
                    break;
                await Task.Delay(100);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Ignore during shutdown
        }
    }
}

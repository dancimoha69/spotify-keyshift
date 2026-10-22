using System.IO.Pipes;
using System.Text;

namespace KeyShiftBridge;

internal sealed class ActivationPipe : IDisposable
{
    private const string PipeName = "KeyShiftBridge.Activation";
    private readonly Action _showWindow;
    private readonly CancellationTokenSource _shutdown = new();

    public ActivationPipe(Action showWindow) => _showWindow = showWindow;

    public void Start() => _ = Task.Run(ListenAsync);

    public static bool RequestShow()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(700);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine("SHOW");
                return true;
            }
            catch (TimeoutException) { }
            catch (IOException) { }

            Thread.Sleep(150);
        }

        return false;
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_shutdown.Token);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 256, leaveOpen: true);
                var command = await reader.ReadLineAsync(_shutdown.Token);
                if (command == "SHOW") _showWindow();
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (IOException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}

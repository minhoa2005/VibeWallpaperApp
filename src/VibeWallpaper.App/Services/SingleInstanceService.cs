#nullable enable
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace VibeWallpaper.App.Services;

public enum SingleInstanceStartResult
{
    Primary,
    SecondaryActivationSent,
}

public interface IActivationDispatcher
{
    Task DispatchAsync(Func<Task> callback);
}

public sealed class InlineActivationDispatcher : IActivationDispatcher
{
    public Task DispatchAsync(Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return callback();
    }
}

public sealed class SingleInstanceService : IAsyncDisposable
{
    private const string ActivateCommand = "Activate";
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly IActivationDispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _mutex;
    private Task? _listener;
    private int _started;

    public SingleInstanceService(
        string instanceNamespace = "VibeWallpaper",
        IActivationDispatcher? dispatcher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceNamespace);
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        _mutexName = $"Local\\{instanceNamespace.Trim()}.{sid}";
        _pipeName = SanitizePipeName($"{instanceNamespace.Trim()}.{sid}");
        _dispatcher = dispatcher ?? new InlineActivationDispatcher();
    }

    public async Task<SingleInstanceStartResult> StartAsync(
        Func<Task> activationCallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activationCallback);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Single-instance ownership can only be established once.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _mutex = new Mutex(initiallyOwned: false, _mutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            await SendActivationAsync(cancellationToken);
            return SingleInstanceStartResult.SecondaryActivationSent;
        }

        _listener = ListenAsync(activationCallback, _shutdown.Token);
        return SingleInstanceStartResult.Primary;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            await _shutdown.CancelAsync();
        }

        if (_listener is not null)
        {
            try
            {
                await _listener.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        _mutex?.Dispose();
        _mutex = null;
        _shutdown.Dispose();
    }

    private async Task SendActivationAsync(CancellationToken cancellationToken)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(TimeSpan.FromSeconds(3), cancellationToken);
        await using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        await writer.WriteLineAsync(ActivateCommand.AsMemory(), cancellationToken);
    }

    private async Task ListenAsync(Func<Task> activationCallback, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, ActivateCommand, StringComparison.Ordinal))
                {
                    try
                    {
                        await _dispatcher.DispatchAsync(activationCallback);
                    }
                    catch
                    {
                        // An activation callback failure must not tear down the primary listener.
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Yield();
            }
        }
    }

    private static string SanitizePipeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/']).ToHashSet();
        return new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Sources;

public sealed class SourceChangeMonitor : IAsyncDisposable
{
    private readonly IStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly ConcurrentDictionary<WallpaperId, byte> _pending = new();
    private readonly Channel<WallpaperId> _invalidations = Channel.CreateUnbounded<WallpaperId>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _registrationGate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WallpaperId> _pathItems = new(StringComparer.OrdinalIgnoreCase);
    private Task? _worker;

    public SourceChangeMonitor(IStateStore stateStore, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        _stateStore = stateStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_worker is not null) throw new InvalidOperationException("Source monitoring has already started.");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        _worker = ProcessInvalidationsAsync(_shutdown.Token);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var state = (await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        var nextPaths = state.Library
            .Where(static item => item.Definition.Source is VideoSource)
            .ToDictionary(
                static item => ((VideoSource)item.Definition.Source).FilePath,
                static item => item.Definition.Id,
                StringComparer.OrdinalIgnoreCase);
        var nextDirectories = nextPaths.Keys
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .Where(Directory.Exists)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_registrationGate)
        {
            foreach (var directory in _watchers.Keys.Where(directory => !nextDirectories.Contains(directory)).ToArray())
            {
                DisposeWatcher(_watchers[directory]);
                _watchers.Remove(directory);
            }

            foreach (var directory in nextDirectories.Where(directory => !_watchers.ContainsKey(directory)))
                _watchers.Add(directory, CreateWatcher(directory));

            _pathItems.Clear();
            foreach (var pair in nextPaths) _pathItems.Add(pair.Key, pair.Value);
        }
    }

    private FileSystemWatcher CreateWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void DisposeWatcher(FileSystemWatcher watcher)
    {
        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnChanged;
        watcher.Created -= OnChanged;
        watcher.Deleted -= OnChanged;
        watcher.Renamed -= OnRenamed;
        watcher.Dispose();
    }

    public async Task InvalidateAsync(WallpaperId id, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
            var item = current.Library.FirstOrDefault(candidate => candidate.Definition.Id == id)
                ?? throw new KeyNotFoundException($"Wallpaper '{id.Value}' was not found in the library.");
            if (item.Validation.Status == SourceValidationStatus.Changed) return;
            var changed = new WallpaperLibraryItem(
                item.Definition,
                item.ThumbnailCachePath,
                item.Video,
                new SourceValidation(
                    SourceValidationStatus.Changed,
                    item.Validation.Stamp,
                    "source.change_hint",
                    _timeProvider.GetUtcNow()));
            var library = current.Library.Select(candidate =>
                candidate.Definition.Id == id ? changed : candidate).ToArray();
            await _stateStore.SaveAsync(
                new PersistedState(
                    current.SchemaVersion, library, current.Assignments, current.Groups, current.AudioOwner),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_registrationGate)
        {
            foreach (var watcher in _watchers.Values) DisposeWatcher(watcher);
            _watchers.Clear();
            _pathItems.Clear();
        }
        _shutdown.Cancel();
        _invalidations.Writer.TryComplete();
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
        }
        _shutdown.Dispose();
        _stateGate.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => QueuePath(args.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        QueuePath(args.OldFullPath);
        QueuePath(args.FullPath);
    }

    private void QueuePath(string path)
    {
        WallpaperId id;
        lock (_registrationGate)
        {
            if (!_pathItems.TryGetValue(Path.GetFullPath(path), out id)) return;
        }
        if (_pending.TryAdd(id, 0)) _invalidations.Writer.TryWrite(id);
    }

    private async Task ProcessInvalidationsAsync(CancellationToken cancellationToken)
    {
        await foreach (var id in _invalidations.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await InvalidateAsync(id, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }
    }
}

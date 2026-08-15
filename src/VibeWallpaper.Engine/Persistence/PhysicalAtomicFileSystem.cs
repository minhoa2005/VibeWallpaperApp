namespace VibeWallpaper.Engine.Persistence;

public sealed class PhysicalAtomicFileSystem : IAtomicFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 4096,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream CreateNew(string path) => new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 4096,
        FileOptions.Asynchronous | FileOptions.WriteThrough);

    public async Task FlushAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (stream is FileStream fileStream)
        {
            fileStream.Flush(flushToDisk: true);
        }
        else
        {
            stream.Flush();
        }
    }

    public void Move(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath);

    public void Replace(string sourcePath, string destinationPath, string backupPath) =>
        File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);

    public void DeleteFile(string path) => File.Delete(path);
}

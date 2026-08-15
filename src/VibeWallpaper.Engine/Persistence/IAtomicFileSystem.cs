namespace VibeWallpaper.Engine.Persistence;

public interface IAtomicFileSystem
{
    bool FileExists(string path);

    Stream OpenRead(string path);

    void CreateDirectory(string path);

    Stream CreateNew(string path);

    Task FlushAsync(Stream stream, CancellationToken cancellationToken);

    void Move(string sourcePath, string destinationPath);

    void Replace(string sourcePath, string destinationPath, string backupPath);

    void DeleteFile(string path);
}

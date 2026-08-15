namespace VibeWallpaper.App.Services;

public interface IUserRunKey
{
    string? Read(string valueName);
    void Write(string valueName, string commandLine);
    void Delete(string valueName);
}

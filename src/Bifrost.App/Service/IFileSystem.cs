namespace Bifrost.App.Service;

/// <summary>The few file operations the installer needs, so its tests touch no real <c>/etc</c>.</summary>
public interface IFileSystem
{
    bool FileExists(string path);

    /// <summary>The files directly in a folder whose names end with <paramref name="suffix"/>.</summary>
    IReadOnlyList<string> FilesEndingWith(string folder, string suffix);

    void CreateDirectory(string path, UnixFileMode mode);

    /// <summary>Writes the whole file, replacing it, with exactly this mode.</summary>
    void WriteFile(string path, string content, UnixFileMode mode);

    /// <summary>Copies over the target even while a program is running from it.</summary>
    void CopyFile(string source, string target, UnixFileMode mode);

    /// <summary>Removes a file or a whole folder; a missing one is fine.</summary>
    void Delete(string path);
}

public sealed class RealFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> FilesEndingWith(string folder, string suffix) =>
        Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*" + suffix).Order(StringComparer.Ordinal)] : [];

    public void CreateDirectory(string path, UnixFileMode mode)
    {
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, mode);
    }

    public void WriteFile(string path, string content, UnixFileMode mode)
    {
        // Written beside the target and moved over it, so nobody reads half a file or one with the wrong mode.
        var temp = path + ".new";
        File.Delete(temp);
        using (var stream = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = mode }))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
        }
        File.SetUnixFileMode(temp, mode); // the umask may have narrowed it
        File.Move(temp, path, overwrite: true);
    }

    public void CopyFile(string source, string target, UnixFileMode mode)
    {
        // A running program's file cannot be written to ("text file busy"), but it can be replaced.
        var temp = target + ".new";
        File.Copy(source, temp, overwrite: true);
        File.SetUnixFileMode(temp, mode);
        File.Move(temp, target, overwrite: true);
    }

    public void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }
}

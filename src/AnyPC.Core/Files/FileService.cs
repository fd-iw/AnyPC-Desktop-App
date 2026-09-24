using AnyPC.Core.Abstractions;

namespace AnyPC.Core.Files;

/// <summary>
/// File access for the paired phone. Paired devices are fully trusted (they can already
/// drive the keyboard and mouse), so this exposes the whole file system the user can reach.
/// </summary>
public sealed class FileService : IFileService
{
    public FsListing List(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            var roots = new List<FsEntry>();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) roots.Add(new FsEntry(home, true, 0, 0));
            foreach (var d in DriveInfo.GetDrives())
            {
                bool ready;
                try { ready = d.IsReady; } catch (IOException) { ready = false; }
                if (!ready || d.DriveType is DriveType.Ram) continue;
                if (!OperatingSystem.IsWindows() && d.DriveType is not DriveType.Fixed and not DriveType.Removable) continue;
                roots.Add(new FsEntry(d.RootDirectory.FullName, true, 0, 0));
            }
            return new FsListing("", "", roots);
        }

        var dir = new DirectoryInfo(Path.GetFullPath(path));
        if (!dir.Exists) throw new DirectoryNotFoundException($"Folder not found: {path}");

        var entries = new List<FsEntry>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var info in dir.EnumerateFileSystemInfos("*", options))
        {
            try
            {
                var isDir = info is DirectoryInfo;
                if (!isDir && info.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                entries.Add(new FsEntry(info.Name, isDir, isDir ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc.Ticks > 0 ? new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds() : 0));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        entries.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new FsListing(dir.FullName, dir.Parent?.FullName ?? "", entries);
    }

    public Stream OpenRead(string path, out string name, out long size)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists) throw new FileNotFoundException($"File not found: {path}");
        name = info.Name;
        size = info.Length;
        return new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
    }

    public Stream Create(string directory, string name, out string finalName)
    {
        var dir = Path.GetFullPath(string.IsNullOrEmpty(directory) ? DefaultUploadFolder() : directory);
        Directory.CreateDirectory(dir);

        var safe = SanitizeName(name);
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (int i = 0; ; i++)
        {
            finalName = i == 0 ? safe : $"{stem} ({i}){ext}";
            var full = Path.Combine(dir, finalName);
            try
            {
                return new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            }
            catch (IOException) when (File.Exists(full) && i < 1000) { }
        }
    }

    public static string DefaultUploadFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Downloads", "AnyPC");
    }

    /// <summary>Strips any directory components and characters that are invalid in file names on Windows.</summary>
    public static string SanitizeName(string name)
    {
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '|', '?', '*' };
        var chars = name.Select(c => invalid.Contains(c) || c < 32 ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        return s is "" or "." or ".." ? "upload" : s;
    }
}

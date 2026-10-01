using System.Diagnostics;
using DiskSweeper.Core;

namespace DiskSweeper.Core.Tests;

/// <summary>
/// A throwaway folder under %TEMP% for tests to scan and delete in. Every destructive test works only
/// inside one of these, never on real folders. Disposing undoes any permission changes and removes it.
/// </summary>
internal sealed class Sandbox : IDisposable
{
    private readonly List<Action> _cleanup = [];

    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "DiskSweeperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relativePath) => Path.Join(Root, relativePath);

    public string CreateFolder(string relativePath)
    {
        string path = PathOf(relativePath);
        Directory.CreateDirectory(LongPath.ToExtended(path));
        return path;
    }

    public string CreateFile(string relativePath, long size, FileAttributes attributes = FileAttributes.Normal, DateTime? modifiedUtc = null)
    {
        string path = PathOf(relativePath);
        Directory.CreateDirectory(LongPath.ToExtended(Path.GetDirectoryName(path)!));
        using (var stream = new FileStream(LongPath.ToExtended(path), FileMode.CreateNew))
        {
            stream.SetLength(size);
        }
        if (modifiedUtc is DateTime time) File.SetLastWriteTimeUtc(LongPath.ToExtended(path), time);
        if (attributes != FileAttributes.Normal) File.SetAttributes(LongPath.ToExtended(path), attributes);
        return path;
    }

    /// <summary>Creates a directory junction (no admin needed) at <paramref name="linkRelative"/> pointing to <paramref name="targetRelative"/>.</summary>
    public string CreateJunction(string linkRelative, string targetRelative)
    {
        string link = PathOf(linkRelative);
        Run("cmd", "/c", "mklink", "/J", link, PathOf(targetRelative));
        // Remove the link itself first so cleanup can never reach through it.
        _cleanup.Add(() => Directory.Delete(link));
        return link;
    }

    /// <summary>Stops the current user from listing a folder, the way protected system folders behave.</summary>
    public void DenyListing(string relativePath)
    {
        string path = PathOf(relativePath);
        string sid = Elevation.CurrentUserSid;
        Run("icacls", path, "/deny", $"*{sid}:(RD)");
        _cleanup.Add(() => Run("icacls", path, "/remove:d", $"*{sid}"));
    }

    public void OnCleanup(Action action) => _cleanup.Add(action);

    public static void Run(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} failed: {output}");
    }

    public void Dispose()
    {
        for (int i = _cleanup.Count - 1; i >= 0; i--)
        {
            try
            {
                _cleanup[i]();
            }
            catch
            {
                // Best effort: one failed cleanup step shouldn't stop the rest.
            }
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(LongPath.ToExtended(Root), "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(LongPath.ToExtended(Root), recursive: true);
        }
        catch
        {
            // Leftovers in %TEMP%\DiskSweeperTests are harmless.
        }
    }
}

/// <summary>A test that only runs when the test process has administrator rights.</summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!Elevation.IsElevated) Skip = "Needs an administrator terminal.";
    }
}

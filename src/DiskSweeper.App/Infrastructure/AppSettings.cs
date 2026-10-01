using System.IO;
using System.Text.Json;
using DiskSweeper.Core.Deletion;

namespace DiskSweeper.App.Infrastructure;

/// <summary>The few choices DiskSweeper remembers between runs, stored next to its log.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Join(ProtectedPaths.AppDataFolder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string LastPath { get; set; } = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public DeleteMethod Method { get; set; } = DeleteMethod.RecycleBin;

    public bool FastScan { get; set; } = true;

    public bool SaveScans { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

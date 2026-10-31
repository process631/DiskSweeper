using System.Globalization;
using System.Text;

namespace DiskSweeper.Core.Deletion;

/// <summary>
/// Appends one CSV line per delete attempt, so there is always a record of what was removed, how, and when.
/// Opens fine in Excel. Default location: %LOCALAPPDATA%\DiskSweeper\deletion-log.csv.
/// </summary>
public sealed class DeletionLog
{
    private const string Header = "Time,Action,Outcome,Bytes,Size,Path,Details";
    private readonly object _gate = new();

    public DeletionLog(string? filePath = null)
    {
        FilePath = filePath ?? Path.Join(ProtectedPaths.AppDataFolder, "deletion-log.csv");
    }

    public string FilePath { get; }

    public void Write(string action, string outcome, long bytes, string path, string? details = null)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
            .Append(Csv(action)).Append(',')
            .Append(Csv(outcome)).Append(',')
            .Append(bytes.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(Csv(ByteSize.Format(bytes, CultureInfo.InvariantCulture))).Append(',')
            .Append(Csv(path)).Append(',')
            .Append(Csv(details ?? ""))
            .AppendLine()
            .ToString();

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            bool isNew = !File.Exists(FilePath);
            File.AppendAllText(FilePath, (isNew ? Header + Environment.NewLine : "") + line, Encoding.UTF8);
        }
    }

    private static string Csv(string value)
    {
        // A file name can start with =, +, - or @. Excel would treat that cell as a formula.
        bool neutralize = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
        if (neutralize) value = "'" + value;

        if (!neutralize && value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

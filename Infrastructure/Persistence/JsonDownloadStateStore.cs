using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Infrastructure.Persistence;

/// <summary>
/// Stores one JSON file per download (<c>&lt;id&gt;.json</c>) in a directory.
/// Writes are atomic (write to a temp file, then rename), so a crash never leaves a half-written file.
/// </summary>
public sealed class JsonDownloadStateStore : IDownloadStateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonDownloadStateStore(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>Default location: <c>$XDG_DATA_HOME/dnm/downloads</c> (usually <c>~/.local/share/dnm/downloads</c>).</summary>
    public static string DefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        return Path.Combine(root, "dnm", "downloads");
    }

    public async Task SaveAsync(DownloadRecord record)
    {
        await _writeLock.WaitAsync();
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(record.Id);
            var temp = path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, record, Json);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DownloadRecord>> LoadAllAsync()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<DownloadRecord>();

        var records = new List<DownloadRecord>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                if (await JsonSerializer.DeserializeAsync<DownloadRecord>(stream, Json) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged file must not stop the app from starting; skip it.
            }
        }
        return records;
    }

    public Task DeleteAsync(Guid id)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(Guid id) => Path.Combine(Directory, id.ToString("N") + ".json");
}

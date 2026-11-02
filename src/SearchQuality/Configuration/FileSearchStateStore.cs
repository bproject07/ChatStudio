using System.Text.Json;
using SearchQuality.OpenAI;

namespace SearchQuality.Configuration;

public static class FileSearchStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task SaveAsync(
        string path,
        FileSearchIndexState state,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    public static async Task<FileSearchIndexState?> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<FileSearchIndexState>(
            stream,
            JsonOptions,
            cancellationToken);
    }
}

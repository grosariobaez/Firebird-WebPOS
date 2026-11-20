using System.Text;
using System.Text.Json;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Security;
using JomliaWebPosSync.Serialization;

namespace JomliaWebPosSync.Services;

public sealed class SyncProgressStore(PayloadDirectorySecurity security) : ISyncProgressStore
{
    private string? directory;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public void Initialize(string dir)
    {
        security.Prepare(dir);
        directory = Path.GetFullPath(dir);
    }

    private string FilePath => Path.Combine(directory ?? throw new InvalidOperationException("Progress store not initialized"), "sync-progress.json");

    public async Task<SyncProgress?> LoadAsync(ImportRoute route, int companyId, int initialId, CancellationToken ct)
    {
        string path = FilePath;
        FileStream file;
        try
        {
            security.ProtectFile(path);
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        try
        {
            await using (file)
            {
                using var reader = new StreamReader(file, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                string text = await reader.ReadToEndAsync(ct);
                var progress = JsonSerializer.Deserialize<SyncProgress>(text, InvoiceJsonSerializer.Options);
                if (progress is null || !progress.Matches(route, companyId)) return null;
                return Normalize(progress, initialId);
            }
        }
        catch { return null; }
    }

    public async Task SaveAsync(SyncProgress progress, CancellationToken ct)
    {
        string destination = FilePath;
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(progress, InvoiceJsonSerializer.Options);
            byte[] bytes = Utf8.GetBytes(json);
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            DurableFileRename.MoveReplace(temp, destination);
            try { security.ProtectFile(destination); } catch { }
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static SyncProgress Normalize(SyncProgress p, int initialId)
    {
        int next = Math.Max(p.NextForwardId, initialId);
        int rec = Math.Clamp(p.ReconcileCursorId, initialId, next);
        var completed = (p.CompletedIds ?? []).Distinct().Where(i => i >= initialId).OrderBy(i => i).ToArray();
        var retry = (p.RetryIds ?? []).Distinct().Where(i => i >= initialId).Except(completed).OrderBy(i => i).ToArray();
        return p with { NextForwardId = next, ReconcileCursorId = rec, CompletedIds = completed, RetryIds = retry };
    }
}

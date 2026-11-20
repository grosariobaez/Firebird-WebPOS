using System.Globalization;
using System.Text;
using System.Text.Json;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Security;
using JomliaWebPosSync.Serialization;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Services;

public sealed class PayloadStore(IOptions<SyncOptions> sync, IOptions<WebPosOptions> webpos,
    PayloadDirectorySecurity security) : IPayloadStore, IDisposable
{
    private string? directory;
    private FileStream? instanceLock;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public void Initialize()
    {
        if (instanceLock is not null) return;
        string path = Environment.ExpandEnvironmentVariables(sync.Value.PayloadDirectory);
        security.Prepare(path);
        directory = Path.GetFullPath(path);
        string completedDir = Path.Combine(directory, "completed");
        if (!System.IO.Directory.Exists(completedDir)) System.IO.Directory.CreateDirectory(completedDir);
        try { security.ProtectFile(completedDir); } catch { }
        // Exclusive handle held for process lifetime. OS releases it after a crash.
        string lockPath = Path.Combine(directory, ".instance.lock");
        if (File.Exists(lockPath)) security.ProtectFile(lockPath);
        instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public string DirectoryPath => directory ?? throw new InvalidOperationException("Payload store not initialized");

    private string FileName(InvoiceKey key) => $"JOMLIA_{key.IdCompania.ToString(CultureInfo.InvariantCulture)}_{key.IdFactura.ToString(CultureInfo.InvariantCulture)}.payload";
    private string PendingPath(InvoiceKey key) => Path.Combine(DirectoryPath, FileName(key));
    private string CompletedDir => Path.Combine(DirectoryPath, "completed");
    private string CompletedPath(InvoiceKey key) => Path.Combine(CompletedDir, FileName(key));

    public IEnumerable<InvoiceKey> EnumeratePendingKeys() => EnumerateIn(DirectoryPath);
    public IEnumerable<InvoiceKey> EnumerateCompletedKeys() => EnumerateIn(CompletedDir);

    private IEnumerable<InvoiceKey> EnumerateIn(string path)
    {
        if (!System.IO.Directory.Exists(path)) yield break;
        foreach (string file in System.IO.Directory.EnumerateFiles(path, "JOMLIA_*.payload", SearchOption.TopDirectoryOnly))
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('_');
            if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int company)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int invoice))
                throw new SyncException("INVALID_PAYLOAD_FILENAME", "PayloadDirectory");
            if (company != webpos.Value.SourceCompanyId) throw new SyncException("PAYLOAD_COMPANY_MISMATCH", "PayloadDirectory");
            yield return new(company, invoice);
        }
    }

    public async Task<StoredPayload?> LoadAsync(InvoiceKey key, CancellationToken ct)
    {
        string pending = PendingPath(key);
        if (File.Exists(pending)) return await ReadPayloadAsync(pending, key, ct);
        string completed = CompletedPath(key);
        if (File.Exists(completed)) return await ReadPayloadAsync(completed, key, ct);
        return null;
    }

    private async Task<StoredPayload> ReadPayloadAsync(string path, InvoiceKey key, CancellationToken ct)
    {
        FileStream file;
        try
        {
            security.ProtectFile(path);
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { throw; }
        await using (file)
        {
            if (file.Length <= 0 || file.Length > sync.Value.MaxPayloadBytes)
                throw new SyncException("PAYLOAD_SIZE_INVALID", "PayloadFile");
            try
            {
                using var reader = new StreamReader(file, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
                string text = await reader.ReadToEndAsync(ct);
                var envelope = JsonSerializer.Deserialize<PayloadEnvelope>(text, InvoiceJsonSerializer.Options);
                if (envelope is null || envelope.Content is null || envelope.Sha256 != InvoiceJsonSerializer.HashUtf8(envelope.Content))
                    throw new SyncException("PAYLOAD_CORRUPT", "envelope.checksum");
                var stored = JsonSerializer.Deserialize<StoredPayload>(envelope.Content, InvoiceJsonSerializer.Options);
                if (stored is null || stored.Version != InvoiceJsonSerializer.FormatVersion || stored.Key != key
                    || stored.Route is null || stored.Route.SourceSystem != webpos.Value.SourceSystem
                    || stored.Payload is null || stored.PayloadSha256 != InvoiceJsonSerializer.HashSqlPayload(stored.Payload)
                    || stored.SourceFingerprint is not { Length: 64 } || stored.PaymentMappings is null)
                    throw new SyncException("PAYLOAD_CORRUPT_OR_INCOMPATIBLE", "payload.checksum/identity/version");
                using var json = JsonDocument.Parse(stored.Payload);
                var root = json.RootElement;
                if (root.GetProperty("idCompania").GetInt32() != key.IdCompania || root.GetProperty("idFactura").GetInt32() != key.IdFactura
                    || root.GetProperty("header").GetProperty("ncf").GetString() != stored.Ncf)
                    throw new SyncException("PAYLOAD_CORRUPT", "payload.identity");
                return stored;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or DecoderFallbackException)
            {
                throw new SyncException("PAYLOAD_CORRUPT", "PayloadFile");
            }
        }
    }

    public async Task<StoredPayload> SaveIfAbsentAsync(StoredPayload payload, CancellationToken ct)
    {
        string pending = PendingPath(payload.Key);
        string completed = CompletedPath(payload.Key);
        if (File.Exists(completed))
        {
            var existingCompleted = await ReadPayloadAsync(completed, payload.Key, ct);
            return RequireSame(existingCompleted, payload);
        }
        if (File.Exists(pending))
        {
            var existing = await ReadPayloadAsync(pending, payload.Key, ct);
            return RequireSame(existing, payload);
        }
        string content = JsonSerializer.Serialize(payload, InvoiceJsonSerializer.Options);
        string envelope = JsonSerializer.Serialize(new PayloadEnvelope(content, InvoiceJsonSerializer.HashUtf8(content)), InvoiceJsonSerializer.Options);
        byte[] bytes = Utf8.GetBytes(envelope);
        if (bytes.Length > sync.Value.MaxPayloadBytes) throw new SyncException("PAYLOAD_SIZE_LIMIT", "PayloadFile");
        string temporary = pending + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            DurableFileRename.MoveNew(temporary, pending);
            return await ReadPayloadAsync(pending, payload.Key, ct);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public Task MarkCompletedAsync(InvoiceKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string pending = PendingPath(key);
        string completed = CompletedPath(key);
        if (File.Exists(completed)) return Task.CompletedTask;
        if (!File.Exists(pending)) return Task.CompletedTask;
        if (!System.IO.Directory.Exists(CompletedDir)) System.IO.Directory.CreateDirectory(CompletedDir);
        try { DurableFileRename.MoveNew(pending, completed); }
        catch (IOException)
        {
            if (File.Exists(completed) && !File.Exists(pending)) return Task.CompletedTask;
            throw;
        }
        return Task.CompletedTask;
    }

    private static StoredPayload RequireSame(StoredPayload existing, StoredPayload candidate)
    {
        if (existing.Payload != candidate.Payload || existing.SourceFingerprint != candidate.SourceFingerprint || existing.Route != candidate.Route)
            throw new SyncException("IMMUTABLE_PAYLOAD_CONFLICT", "PayloadFile");
        return existing;
    }

    public void Dispose() => instanceLock?.Dispose();
}

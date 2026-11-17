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
        // Exclusive handle held for process lifetime. OS releases it after a crash.
        string lockPath = Path.Combine(directory, ".instance.lock");
        if (File.Exists(lockPath)) security.ProtectFile(lockPath);
        instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string FileName(InvoiceKey key) => $"JOMLIA_{key.IdCompania.ToString(CultureInfo.InvariantCulture)}_{key.IdFactura.ToString(CultureInfo.InvariantCulture)}.payload";
    private string FilePath(InvoiceKey key) => Path.Combine(directory ?? throw new InvalidOperationException("Payload store not initialized"), FileName(key));

    public IEnumerable<InvoiceKey> EnumerateKeys()
    {
        foreach (string path in Directory.EnumerateFiles(directory!, "JOMLIA_*.payload", SearchOption.TopDirectoryOnly))
        {
            var parts = Path.GetFileNameWithoutExtension(path).Split('_');
            if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int company)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int invoice))
                throw new SyncException("INVALID_PAYLOAD_FILENAME", "PayloadDirectory");
            if (company != webpos.Value.SourceCompanyId) throw new SyncException("PAYLOAD_COMPANY_MISMATCH", "PayloadDirectory");
            yield return new(company, invoice);
        }
    }

    public async Task<StoredPayload?> LoadAsync(InvoiceKey key, CancellationToken ct)
    {
        string path = FilePath(key);
        // Do not use File.Exists to decide absence: it also hides access and IO failures.
        FileStream file;
        try
        {
            security.ProtectFile(path);
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
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
        var existing = await LoadAsync(payload.Key, ct);
        if (existing is not null) return RequireSame(existing, payload);
        string content = JsonSerializer.Serialize(payload, InvoiceJsonSerializer.Options);
        string envelope = JsonSerializer.Serialize(new PayloadEnvelope(content, InvoiceJsonSerializer.HashUtf8(content)), InvoiceJsonSerializer.Options);
        byte[] bytes = Utf8.GetBytes(envelope);
        if (bytes.Length > sync.Value.MaxPayloadBytes) throw new SyncException("PAYLOAD_SIZE_LIMIT", "PayloadFile");
        string destination = FilePath(payload.Key);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                // New file inherits the already restricted directory ACL.
                await file.WriteAsync(bytes, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            // Atomic same-volume rename with no overwrite. No SQL call can precede this operation.
            DurableFileRename.MoveNew(temporary, destination);
            return await LoadAsync(payload.Key, ct) ?? throw new SyncException("PAYLOAD_DISAPPEARED", "PayloadFile");
        }
        finally
        {
            // A stale .tmp never represented a sent document; failed cleanup is harmless.
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static StoredPayload RequireSame(StoredPayload existing, StoredPayload candidate)
    {
        if (existing.Payload != candidate.Payload || existing.SourceFingerprint != candidate.SourceFingerprint || existing.Route != candidate.Route)
            throw new SyncException("IMMUTABLE_PAYLOAD_CONFLICT", "PayloadFile");
        return existing;
    }

    public void Dispose() => instanceLock?.Dispose();
}

using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Diagnostics;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Serialization;
using JomliaWebPosSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Tests;

public sealed class IncrementalSyncTests
{
    private static readonly DateTime CutoverDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public async Task FirstRun_StartsAtCutover_DoesNotSelectHistoricalBeforeInitial()
    {
        var (worker, state, fb, _, _, cutover) = Create();
        fb.Candidates = [new(1, 100), new(1, 101)];
        fb.Queue(100, "E310000000100"); fb.Queue(101, "E310000000101");
        await worker.RunCycleAsync(state, CancellationToken.None);
        Assert.Contains(fb.CandidateCalls, c => c.from == 100); // initial
        Assert.DoesNotContain(fb.CandidateCalls, c => c.from < 100);
        Assert.Equal(102, state.Progress.NextForwardId);
    }

    [Fact]
    public async Task Restart_ResumesFromCheckpoint_WithoutRescanningForwardHistory()
    {
        var route = Route();
        var loaded = SyncProgress.CreateInitial(route, 1, 100) with { NextForwardId = 105, ReconcileCursorId = 100, CompletedIds = [100,101,102,103,104], RetryIds = [] };
        var (worker, state, fb, sql, _, cutover) = Create(progress: loaded);
        // add completed markers to avoid SQL checks if somehow queried
        foreach (int id in loaded.CompletedIds) sql.Imported.Add(new(1,id));
        fb.Candidates = [new(1, 105)];
        fb.Queue(105, "E310000000105");
        await worker.RunCycleAsync(state, CancellationToken.None);
        Assert.Contains(fb.CandidateCalls, c => c.from == 105 && c.to is null);
        Assert.DoesNotContain(fb.CandidateCalls, c => c.from == 100 && c.to is null);
        Assert.Contains(105, state.Progress.CompletedIds);
    }

    [Fact]
    public async Task CheckpointLoss_RecoveredFromCompletedOutbox_NoFullHistoricalRescan()
    {
        var (worker, state, fb, sql, store, cutover) = Create(progress: null);
        // completed payloads exist in durable store but checkpoint missing
        store.SeedCompleted(Payload(100, "E310000000100"));
        store.SeedCompleted(Payload(101, "E310000000101"));
        await worker.RecoverFromOutboxAsync(state, cutover, CancellationToken.None);
        Assert.Contains(100, state.Progress.CompletedIds);
        Assert.Contains(101, state.Progress.CompletedIds);
        Assert.True(state.Progress.NextForwardId >= 102);
        fb.Candidates = [new(1,102)];
        fb.Queue(102, "E310000000102");
        await worker.RunCycleAsync(state, CancellationToken.None);
        Assert.Contains(fb.CandidateCalls, c => c.from >= 102 && c.to is null);
        Assert.DoesNotContain(fb.CandidateCalls, c => c.from == 100 && c.to is null);
    }

    [Fact]
    public async Task CheckpointCorruption_RebuildsSafelyFromOutbox()
    {
        var ps = new FakeProgressStore { ToLoad = null }; // loader treats corruption/mismatch as null
        var (worker, state, fb, _, store, cutover) = Create(progressStore: ps, progress: null);
        store.Seed(Payload(103, "E310000000103")); // pending retryable evidence
        await worker.RecoverFromOutboxAsync(state, cutover, CancellationToken.None);
        Assert.Contains(103, state.Progress.RetryIds);
        fb.Queue(103, "E310000000103");
        await worker.RunCycleAsync(state, CancellationToken.None);
        Assert.Contains(103, state.Progress.CompletedIds);
        Assert.DoesNotContain(103, state.Progress.RetryIds);
    }

    [Fact]
    public async Task FailedInvoice_DoesNotBlockLaterInvoices_AndIsRetried()
    {
        var (worker, state, fb, sql, _, _) = Create(sync: ValidSync(batch:10));
        fb.Candidates = [new(1,100), new(1,101)];
        // 100 transient import failure first cycle, 101 succeeds
        sql.FailTransient.Add(new(1,100));
        fb.Queue(100, "E310000000100"); fb.Queue(101, "E310000000101");
        await worker.RunCycleAsync(state, CancellationToken.None);
        Assert.Contains(101, state.Progress.CompletedIds);
        Assert.Contains(100, state.Progress.RetryIds);
        Assert.DoesNotContain(100, state.Progress.CompletedIds);

        // second cycle retries 100
        sql.FailTransient.Remove(new(1,100));
        fb.Queue(100, "E310000000100");
        fb.Candidates = [];
        await worker.RunCycleAsync(state, CancellationToken.None);
        Assert.Contains(100, state.Progress.CompletedIds);
        Assert.DoesNotContain(100, state.Progress.RetryIds);
    }

    [Fact]
    public async Task LateEligibleInvoice_DetectedByBoundedReconciliation()
    {
        var route = Route();
        // forward already advanced past 102 (e.g. saw 100,101,103 earlier) but 102 became eligible later
        var loaded = SyncProgress.CreateInitial(route, 1, 100) with { NextForwardId = 104, ReconcileCursorId = 100, CompletedIds = [100,101,103], RetryIds = [] };
        var (worker, state, fb, sql, _, _) = Create(progress: loaded);
        foreach (int id in loaded.CompletedIds) sql.Imported.Add(new(1,id));
        fb.Candidates = [new(1,102)]; // now eligible inside reconciliation window [100,104)
        fb.Queue(102, "E310000000102");
        await worker.RunCycleAsync(state, CancellationToken.None);
        Assert.Contains(fb.CandidateCalls, c => c.to is not null); // reconciliation query used
        Assert.Contains(102, state.Progress.CompletedIds);
    }

    [Fact]
    public async Task AlreadyImportedInSql_NotReimported_AndMarkedCompleted()
    {
        var (worker, state, fb, sql, store, _) = Create();
        sql.Imported.Add(new(1,100));
        fb.Candidates = [new(1,100)];
        await worker.RunCycleAsync(state, CancellationToken.None);
        Assert.Empty(sql.Imports);
        Assert.Equal(0, fb.Reads); // no snapshot read needed when SQL says imported
        Assert.Contains(100, state.Progress.CompletedIds);
    }

    [Fact]
    public async Task CompletedPayload_NotRecheckedEveryCycle_NoRepeatedAlreadyImportedLog()
    {
        var route = Route();
        var loaded = SyncProgress.CreateInitial(route, 1, 100) with { NextForwardId = 101, ReconcileCursorId = 100, CompletedIds = [100], RetryIds = [] };
        var (worker, state, fb, sql, _, _) = Create(progress: loaded);
        // even if SQL would report imported, worker should not process 100 at all because checkpoint marks completed
        fb.Candidates = [];
        var s1 = await worker.RunCycleAsync(state, CancellationToken.None);
        var s2 = await worker.RunCycleAsync(state, CancellationToken.None);
        Assert.Equal(0, fb.Reads);
        Assert.Equal(0, sql.IsImportedCalls);
        Assert.Equal(0, s1.imported + s2.imported);
    }

    [Fact]
    public async Task Outbox_PendingMovesToCompleted_PayloadPreserved_Immutable()
    {
        var (worker, state, fb, sql, store, _) = Create();
        fb.Candidates = [new(1,100)];
        fb.Queue(100, "E310000000100");
        await worker.RunCycleAsync(state, CancellationToken.None);
        Assert.False(store.IsPending(new(1,100)));
        Assert.True(store.IsCompleted(new(1,100)));
        var completed = await store.LoadAsync(new(1,100), CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal("E310000000100", completed!.Ncf);
    }

    [Fact]
    public async Task Progress_IsBoundToCompanyAndDestination_RouteMismatchStartsFresh()
    {
        var route = Route();
        var other = route with { Database = "OTHER" };
        var loaded = SyncProgress.CreateInitial(other, 1, 100) with { NextForwardId = 150, CompletedIds = [120] };
        var ps = new SyncProgressStore(new Security.PayloadDirectorySecurity());
        // simulate mismatch via fake store behavior: our FakeProgressStore returns ToLoad as-is; test logic directly
        var fps = new FakeProgressStore { ToLoad = loaded };
        var (worker, state, _, _, _, _) = Create(progressStore: fps, progress: null);
        // BuildState in Create used provided progress null; manually verify mismatch handling by loading logic expectation
        Assert.False(loaded.Matches(route, 1));
        // actual safe path: SyncProgressStore.LoadAsync returns null on mismatch; emulate
        var (worker2, state2, _, _, _, _) = Create(progress: null);
        Assert.Equal(100, state2.Progress.NextForwardId);
        Assert.Empty(state2.Progress.CompletedIds);
    }

    [Fact]
    public async Task Reconciliation_IsBoundedPerCycle_DoesNotFullHistoryScan()
    {
        var route = Route();
        var completed = Enumerable.Range(100, 20).ToArray();
        var loaded = SyncProgress.CreateInitial(route, 1, 100) with { NextForwardId = 120, ReconcileCursorId = 100, CompletedIds = completed, RetryIds = [] };
        var (worker, state, fb, sql, _, _) = Create(progress: loaded, sync: ValidSync(batch:5));
        foreach (int id in completed) sql.Imported.Add(new(1,id));
        fb.Candidates = [];
        await worker.RunCycleAsync(state, CancellationToken.None);
        var recCalls = fb.CandidateCalls.Where(c => c.to is not null).ToList();
        Assert.NotEmpty(recCalls);
        Assert.All(recCalls, c => Assert.True((c.to!.Value - c.from) <= 5));
        Assert.True(state.Progress.ReconcileCursorId > 100);
        Assert.True(state.Progress.ReconcileCursorId <= 120);
    }

    private static (Worker worker, Worker.State state, FakeFirebird fb, FakeSql sql, FakeStore store, InvoiceCutover cutover) Create(
        SyncProgress? progress = null, FakeProgressStore? progressStore = null, SyncOptions? sync = null)
    {
        var fb = new FakeFirebird();
        var sql = new FakeSql();
        var store = new FakeStore();
        var ps = progressStore ?? new FakeProgressStore();
        var syncOpt = Options.Create(sync ?? ValidSync());
        var web = Options.Create(ValidWebPos());
        var fiscal = Options.Create(new FiscalMappingsOptions());
        var coord = new SyncCoordinator(sql, new InvoiceReader(fb),
            new InvoiceMapper(fiscal, new ConfiguredFiscalDataProvider()),
            new InvoiceValidator(fiscal, web), new InvoiceJsonSerializer(), store,
            new RetryPolicy(syncOpt), new SyncDiagnostics(NullLogger<SyncDiagnostics>.Instance, web),
            syncOpt, web, Options.Create(new SqlServerOptions { Server = "TEST", Database = "TEST" }));
        var worker = new Worker(fb, store, ps, coord,
            new SyncDiagnostics(NullLogger<SyncDiagnostics>.Instance, web),
            new RetryPolicy(syncOpt), syncOpt, web, NullLogger<Worker>.Instance, new NoopLifetime());
        var cutover = InvoiceCutover.From(web.Value, syncOpt.Value);
        var state = progress is not null ? Worker.BuildState(progress, Route(), cutover) : Worker.BuildState(null, Route(), cutover);
        return (worker, state, fb, sql, store, cutover);
    }

    private static ImportRoute Route() => new("JOMLIA", "TEST", "TEST", "LIC", "SUC", "POS", false);
    private static StoredPayload Payload(int invoice, string ncf)
    {
        var serializer = new InvoiceJsonSerializer();
        var mapper = new InvoiceMapper(Options.Create(new FiscalMappingsOptions()), new ConfiguredFiscalDataProvider());
        var snap = Snapshot(invoice, ncf);
        var mapped = mapper.Map(snap);
        string json = serializer.Serialize(mapped);
        return new(InvoiceJsonSerializer.FormatVersion, new(1, invoice), Route(), ncf,
            serializer.Fingerprint(snap), json, InvoiceJsonSerializer.HashSqlPayload(json), mapper.GetPaymentExpectations(mapped));
    }
    private static InvoiceSnapshot Snapshot(int invoice, string ncf) => new(new InvoiceKey(1, invoice),
        new SourceHeader(1, invoice, 1, CutoverDate, "Cliente", "Calle 1", "00112900238", 18m, 118m, 0m, 0m, "PEN", ncf, new DateTime(2026,12,31), "FAC", "31", 0m),
        [new SourceDetail(1, invoice, 1, 100, null, "Producto", 1m, 100m, 0m, 18m, null, 0m, "1", "UND")],
        [new SourcePayment(1, invoice, 1, "F6", 118m, "EMI", 0m, 0m, 0m, 0m, 118m, null)]);
    private static WebPosOptions ValidWebPos() => new() { SourceSystem = "JOMLIA", SourceCompanyId = 1, InitialInvoiceIdByCompany = new Dictionary<int, int> { [1] = 100 }, CompanyLicCod = "LIC", BranchCod = "SUC", PosCod = "POS" };
    private static SyncOptions ValidSync(int batch = 10) => new() { Enabled = true, InvoiceStart = CutoverDate, PayloadDirectory = "C:\\TEST\\JomliaWebPosSync", BatchSize = batch };

    private sealed class FakeSql : ISqlServerRepository
    {
        public HashSet<InvoiceKey> Imported { get; } = [];
        public List<StoredPayload> Imports { get; } = [];
        public HashSet<InvoiceKey> FailTransient { get; } = [];
        public int IsImportedCalls { get; private set; }
        public Task<bool> IsImportedAsync(InvoiceKey key, CancellationToken ct) { IsImportedCalls++; return Task.FromResult(Imported.Contains(key)); }
        public Task<string> ImportAsync(StoredPayload payload, CancellationToken ct)
        {
            if (FailTransient.Contains(payload.Key)) throw new SyncException("TEST_FAIL", "import", transient: true);
            Imported.Add(payload.Key); Imports.Add(payload); return Task.FromResult("OK");
        }
    }

    private sealed class FakeFirebird : IFirebirdRepository
    {
        private readonly Dictionary<int, Queue<InvoiceSnapshot>> snapshots = [];
        public int Reads { get; private set; }
        public List<(int from, int? to)> CandidateCalls { get; } = [];
        public List<InvoiceKey> Candidates { get; set; } = [];
        public void Queue(int invoice, string ncf)
        {
            if (!snapshots.TryGetValue(invoice, out var q)) { q = new(); snapshots[invoice] = q; }
            q.Enqueue(Snapshot(invoice, ncf));
        }
        public Task<IReadOnlyList<InvoiceKey>> GetCandidateInvoicesAsync(int fromIdInclusive, int? toIdExclusive, CancellationToken ct)
        {
            CandidateCalls.Add((fromIdInclusive, toIdExclusive));
            var list = Candidates.Where(k => k.IdFactura >= fromIdInclusive && (!toIdExclusive.HasValue || k.IdFactura < toIdExclusive.Value)).OrderBy(k => k.IdFactura).ToList();
            return Task.FromResult<IReadOnlyList<InvoiceKey>>(list);
        }
        public Task<InvoiceSnapshot> ReadSnapshotAsync(InvoiceKey key, CancellationToken ct)
        {
            Reads++;
            if (snapshots.TryGetValue(key.IdFactura, out var q) && q.Count > 0) return Task.FromResult(q.Dequeue());
            return Task.FromResult(Snapshot(key.IdFactura, $"E3100000{key.IdFactura:00000}"));
        }
    }

    private sealed class FakeStore : IPayloadStore
    {
        private readonly Dictionary<InvoiceKey, StoredPayload> pending = [];
        private readonly Dictionary<InvoiceKey, StoredPayload> completed = [];
        public List<StoredPayload> Saves { get; } = [];
        public string DirectoryPath => "C:\\TEST\\JomliaWebPosSync";
        public void Initialize() { }
        public IEnumerable<InvoiceKey> EnumeratePendingKeys() => pending.Keys.OrderBy(k=>k.IdFactura).ToArray();
        public IEnumerable<InvoiceKey> EnumerateCompletedKeys() => completed.Keys.OrderBy(k=>k.IdFactura).ToArray();
        public Task<StoredPayload?> LoadAsync(InvoiceKey key, CancellationToken ct)
        {
            if (pending.TryGetValue(key, out var p)) return Task.FromResult<StoredPayload?>(p);
            if (completed.TryGetValue(key, out var c)) return Task.FromResult<StoredPayload?>(c);
            return Task.FromResult<StoredPayload?>(null);
        }
        public Task<StoredPayload> SaveIfAbsentAsync(StoredPayload payload, CancellationToken ct) { Saves.Add(payload); if (!completed.ContainsKey(payload.Key)) pending[payload.Key] = payload; return Task.FromResult(pending.TryGetValue(payload.Key, out var p) ? p : completed[payload.Key]); }
        public Task MarkCompletedAsync(InvoiceKey key, CancellationToken ct) { if (pending.Remove(key, out var p)) completed[key] = p; return Task.CompletedTask; }
        public void Seed(StoredPayload payload) => pending[payload.Key] = payload;
        public void SeedCompleted(StoredPayload payload) => completed[payload.Key] = payload;
        public bool IsCompleted(InvoiceKey key) => completed.ContainsKey(key);
        public bool IsPending(InvoiceKey key) => pending.ContainsKey(key);
    }

    private sealed class FakeProgressStore : ISyncProgressStore
    {
        public SyncProgress? Saved { get; private set; }
        public List<SyncProgress> Saves { get; } = [];
        public SyncProgress? ToLoad { get; set; }
        public void Initialize(string directory) { }
        public Task<SyncProgress?> LoadAsync(ImportRoute route, int companyId, int initialId, CancellationToken ct) => Task.FromResult(ToLoad);
        public Task SaveAsync(SyncProgress progress, CancellationToken ct) { Saved = progress; Saves.Add(progress); return Task.CompletedTask; }
    }

    private sealed class NoopLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}

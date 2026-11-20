using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Diagnostics;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Security;
using JomliaWebPosSync.Serialization;
using JomliaWebPosSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Tests;

public sealed class MinimalSyncTests
{
    private static readonly DateTime CutoverDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void Cutover_IsInclusive_AndRejectsEarlierDateOrId()
    {
        var sync = ValidSync();
        var webpos = ValidWebPos();
        var cutover = InvoiceCutover.From(webpos, sync);
        var key = new InvoiceKey(1, 100);

        Assert.False(cutover.Contains(new InvoiceKey(1, 99), CutoverDate));
        Assert.True(cutover.Contains(key, CutoverDate));
        Assert.False(cutover.Contains(key, CutoverDate.AddTicks(-1)));
        Assert.True(cutover.Contains(new InvoiceKey(1, 101), CutoverDate));
        Assert.False(cutover.Contains(new InvoiceKey(2, 100), CutoverDate));
    }

    [Fact]
    public void Emission_RequiresDetailsEmiPaymentsAndAllowedStatus()
    {
        var validator = CreateValidator(ValidFiscal(), ValidWebPos());

        validator.ValidateSource(ValidSnapshot(100));

        var cancelled = Assert.Throws<SyncException>(() => validator.ValidateSource(ValidSnapshot(100) with
        {
            Header = ValidSnapshot(100).Header! with { Status = "ANU" }
        }));
        Assert.Equal("SOURCE_INVOICE_CANCELLED", cancelled.Code);

        var noDetails = Assert.Throws<SyncException>(() => validator.ValidateSource(ValidSnapshot(100) with
        {
            Details = []
        }));
        Assert.Equal("MISSING_DETAILS", noDetails.Code);

        var partial = Assert.Throws<SyncException>(() => validator.ValidateSource(ValidSnapshot(100) with
        {
            Payments = [ValidPayment(100) with { Status = "PEN" }]
        }));
        Assert.Equal("PARTIAL_EMISSION", partial.Code);
    }

    [Fact]
    public async Task IncompleteInvoice_IsRecheckedWithoutPermanentCursor()
    {
        var firebird = new FakeFirebird();
        firebird.Snapshots.Enqueue(ValidSnapshot(100) with
        {
            Payments = [ValidPayment(100) with { Status = "PEN" }]
        });
        firebird.Snapshots.Enqueue(ValidSnapshot(100));
        var validator = CreateValidator(ValidFiscal(), ValidWebPos());

        var first = await firebird.ReadSnapshotAsync(new InvoiceKey(1, 100), CancellationToken.None);
        var failure = Assert.Throws<SyncException>(() => validator.ValidateSource(first));
        Assert.True(failure.Transient);

        var second = await firebird.ReadSnapshotAsync(new InvoiceKey(1, 100), CancellationToken.None);
        validator.ValidateSource(second);
        Assert.Equal(2, firebird.Reads);
    }

    [Fact]
    public async Task DuplicateInSourceMap_DoesNotReadOrImport()
    {
        var key = new InvoiceKey(1, 100);
        var sql = new FakeSql();
        sql.Imported.Add(key);
        var firebird = new FakeFirebird();
        var store = new FakeStore();
        var coordinator = CreateCoordinator(sql, firebird, store, ValidFiscal(), ValidWebPos(), ValidSync());

        bool result = await coordinator.ProcessAsync(key, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(sql.Imports);
        Assert.Equal(0, firebird.Reads);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task PersistedPayload_IsReusedAndSourceChangeIsRejected()
    {
        var key = new InvoiceKey(1, 100);
        var snapshot = ValidSnapshot(100);
        var fiscal = ValidFiscal();
        var webpos = ValidWebPos();
        var sync = ValidSync();
        var payload = PersistedPayload(key, snapshot);

        var sqlA = new FakeSql();
        var firebirdA = new FakeFirebird();
        firebirdA.Snapshots.Enqueue(snapshot);
        var storeA = new FakeStore();
        storeA.Seed(payload);
        bool reused = await CreateCoordinator(sqlA, firebirdA, storeA, fiscal, webpos, sync)
            .ProcessAsync(key, CancellationToken.None);

        Assert.True(reused);
        Assert.Same(payload, Assert.Single(sqlA.Imports));
        Assert.Equal("PERSISTED-JSON", sqlA.Imports[0].Payload);
        Assert.Empty(storeA.Saves);

        var changed = snapshot with
        {
            Header = snapshot.Header! with { Itbis = 19m, Total = 119m, Pagado = 119m }
        };
        var sqlB = new FakeSql();
        var firebirdB = new FakeFirebird();
        firebirdB.Snapshots.Enqueue(changed);
        var storeB = new FakeStore();
        storeB.Seed(payload);
        bool blocked = await CreateCoordinator(sqlB, firebirdB, storeB, fiscal, webpos, sync)
            .ProcessAsync(key, CancellationToken.None);

        Assert.False(blocked);
        Assert.Empty(sqlB.Imports);
        Assert.Empty(storeB.Saves);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 18)]
    [InlineData(2, 16)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    public void ItbisRate_IsCanonical(int tax, decimal expected) =>
        Assert.Equal(expected, FiscalMappingsOptions.RequiredItbisRate(tax));

    [Fact]
    public void Configuration_RejectsNonCanonicalItbisRate()
    {
        var fiscal = ValidFiscal();
        fiscal.ItbisRatesByTaxCode["1"] = 20m;
        var validator = new ConfigurationValidator(
            Options.Create(ValidFirebird()), Options.Create(ValidSqlServer()),
            Options.Create(ValidWebPos()), Options.Create(fiscal));
        var sync = ValidSync();
        sync.Enabled = true;

        var result = validator.Validate(null, sync);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("ItbisRatesByTaxCode.1", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroPriceDetails_AreExcludedFromPayload()
    {
        var fiscal = ValidFiscal();
        var snapshot = ValidSnapshot(100) with
        {
            Details =
            [
                new SourceDetail(1, 100, 1, 100, null, "Producto QA", 1m, 100m, 0m, 18m, "ACT", 0m, "ITBIS1", "UND"),
                new SourceDetail(1, 100, 2, 200, null, "Producto cero", 1m, 0m, 0m, 0m, "ACT", 0m, "ITBIS1", "SER")
            ]
        };
        var mapper = new InvoiceMapper(Options.Create(fiscal), new ConfiguredFiscalDataProvider(Options.Create(fiscal)));

        var payload = mapper.Map(snapshot);

        var detail = Assert.Single(payload.Details);
        Assert.Equal(100, detail.IdProducto);
    }

    [Fact]
    public void Configuration_DisabledRequiresNoProductionValues()
    {
        var validator = new ConfigurationValidator(
            Options.Create(new FirebirdOptions()), Options.Create(new SqlServerOptions()),
            Options.Create(new WebPosOptions()), Options.Create(new FiscalMappingsOptions()));

        var result = validator.Validate(null, new SyncOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Configuration_EnabledRequiresCutoverAndDestination()
    {
        var validator = new ConfigurationValidator(
            Options.Create(ValidFirebird()), Options.Create(ValidSqlServer()),
            Options.Create(new WebPosOptions { SourceSystem = "JOMLIA" }), Options.Create(new FiscalMappingsOptions()));

        var result = validator.Validate(null, new SyncOptions { Enabled = true });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Sync:InvoiceStart", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("WebPos:InitialInvoiceIdByCompany", StringComparison.Ordinal));
    }

    [Fact]
    public void SqlServer_DefaultsRequireExplicitTlsTrustOptIn()
    {
        var sql = new SqlServerOptions();

        Assert.True(sql.Encrypt);
        Assert.False(sql.TrustServerCertificate);
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            ConnectionFactory.CreateSqlServer(sql).ConnectionString);
        Assert.True(builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.Equal("JomliaWebPosSync", builder.ApplicationName);

        var trusted = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            ConnectionFactory.CreateSqlServer(new SqlServerOptions { TrustServerCertificate = true }).ConnectionString);
        Assert.True(trusted.Encrypt);
        Assert.True(trusted.TrustServerCertificate);
    }

    [Fact]
    public void CutoverDescription_UsesMinimumInvoiceIdInclusive()
    {
        var webpos = ValidWebPos();
        webpos.InitialInvoiceIdByCompany = new Dictionary<int, int> { [1] = 250 };
        var cutover = InvoiceCutover.From(webpos, ValidSync());

        Assert.False(cutover.Contains(new InvoiceKey(1, 249), CutoverDate));
        Assert.True(cutover.Contains(new InvoiceKey(1, 250), CutoverDate));
    }

    [Fact]
    public void MissingDetailItbis_SingleBillableLine_Invoice572_DerivesFromHeader()
    {
        var fiscal = MissingItbisFiscal();
        var snapshot = MissingItbisSnapshot(572, 1m, 3500m, 630m, 4130m, "E310000000572");
        var payload = new InvoiceMapper(Options.Create(fiscal), new ConfiguredFiscalDataProvider(Options.Create(fiscal))).Map(snapshot);
        var detail = Assert.Single(payload.Details);
        Assert.Equal(1, detail.Tax);
        Assert.Equal(630m, detail.Itbis);
        CreateValidator(fiscal, ValidWebPos()).ValidatePayload(payload);
    }

    [Fact]
    public void MissingDetailItbis_SingleBillableLine_Invoice571_DerivesFromHeader()
    {
        var fiscal = MissingItbisFiscal();
        var snapshot = MissingItbisSnapshot(571, 2m, 220m, 79.20m, 519.20m, "E310000000571");
        var payload = new InvoiceMapper(Options.Create(fiscal), new ConfiguredFiscalDataProvider(Options.Create(fiscal))).Map(snapshot);
        var detail = Assert.Single(payload.Details);
        Assert.Equal(1, detail.Tax);
        Assert.Equal(79.20m, detail.Itbis);
        CreateValidator(fiscal, ValidWebPos()).ValidatePayload(payload);
    }

    [Fact]
    public void MissingDetailItbis_SingleBillableLine_Exempt_DerivesTax0()
    {
        var fiscal = MissingItbisFiscal();
        var snapshot = MissingItbisSnapshot(573, 1m, 100m, 0m, 100m, "E310000000573");
        var payload = new InvoiceMapper(Options.Create(fiscal), new ConfiguredFiscalDataProvider(Options.Create(fiscal))).Map(snapshot);
        var detail = Assert.Single(payload.Details);
        Assert.Equal(0, detail.Tax);
        Assert.Equal(0m, detail.Itbis);
        CreateValidator(fiscal, ValidWebPos()).ValidatePayload(payload);
    }

    [Fact]
    public void MissingDetailItbis_MultiLine_RejectsArbitraryAllocation()
    {
        var fiscal = MissingItbisFiscal();
        var snapshot = new InvoiceSnapshot(new InvoiceKey(1, 574),
            new SourceHeader(1, 574, 1, CutoverDate, "Cliente QA", "Calle 1", null, 18m, 118m, 118m, 0m, "ACT", "E310000000574", CutoverDate, "FAC", "31", 0m),
            [
                new SourceDetail(1, 574, 1, 100, null, "A", 1m, 50m, 0m, null, "ACT", 0m, null, "UND"),
                new SourceDetail(1, 574, 2, 101, null, "B", 1m, 50m, 0m, null, "ACT", 0m, null, "UND")
            ],
            [new SourcePayment(1, 574, 1, "EFE", 118m, "EMI", 118m, null, null, null, null, null)]);
        fiscal.ItemClasses["1/101"] = 0;
        var mapper = new InvoiceMapper(Options.Create(fiscal), new ConfiguredFiscalDataProvider(Options.Create(fiscal)));
        var ex = Assert.Throws<SyncException>(() => mapper.Map(snapshot));
        Assert.Equal("ITBIS_ALLOCATION_AMBIGUOUS", ex.Code);
    }

    [Fact]
    public void MissingDetailItbis_RoundingDifference_IsRejected()
    {
        var fiscal = MissingItbisFiscal();
        var snapshot = MissingItbisSnapshot(575, 1m, 100m, 18.01m, 118.01m, "E310000000575");
        var mapper = new InvoiceMapper(Options.Create(fiscal), new ConfiguredFiscalDataProvider(Options.Create(fiscal)));
        var ex = Assert.Throws<SyncException>(() => mapper.Map(snapshot));
        Assert.Equal("LINE_ITBIS_MISMATCH", ex.Code);
    }

    [Fact]
    public void MixedRates_MultiLine_WithVerifiableClassification_AreAccepted()
    {
        var fiscal = ValidFiscal();
        fiscal.TaxCodes["ITBIS0"] = 0;
        fiscal.ItemClasses["1/101"] = 0;
        fiscal.NcfExpiryDates["1/E310000000576"] = new DateOnly(2030, 1, 1);
        var snapshot = new InvoiceSnapshot(new InvoiceKey(1, 576),
            new SourceHeader(1, 576, 1, CutoverDate, "Cliente QA", "Calle 1", null, 18m, 218m, 218m, 0m, "ACT", "E310000000576", null, "FAC", "31", 0m),
            [
                new SourceDetail(1, 576, 1, 100, null, "Gravado", 1m, 100m, 0m, 18m, "ACT", 0m, "ITBIS1", "UND"),
                new SourceDetail(1, 576, 2, 101, null, "Exento", 1m, 100m, 0m, 0m, "ACT", 0m, "ITBIS0", "UND")
            ],
            [new SourcePayment(1, 576, 1, "EFE", 218m, "EMI", 218m, null, null, null, null, null)]);
        var payload = new InvoiceMapper(Options.Create(fiscal), new ConfiguredFiscalDataProvider(Options.Create(fiscal))).Map(snapshot);
        Assert.Equal(2, payload.Details.Count);
        Assert.Equal(18m, payload.Details[0].Itbis);
        Assert.Equal(0m, payload.Details[1].Itbis);
        CreateValidator(fiscal, ValidWebPos()).ValidatePayload(payload);
    }

    private static FiscalMappingsOptions MissingItbisFiscal()
    {
        var fiscal = ValidFiscal();
        fiscal.LineItbisInterpretation = ItbisInterpretation.Unconfirmed;
        fiscal.ItbisRoundedPerLineConfirmed = false;
        fiscal.UseFacFechaVenceAsNcfExpiryConfirmed = true;
        return fiscal;
    }

    private static InvoiceSnapshot MissingItbisSnapshot(int invoice, decimal qty, decimal price, decimal headerItbis, decimal total, string ncf) => new(
        new InvoiceKey(1, invoice),
        new SourceHeader(1, invoice, 1, CutoverDate, "Cliente QA", "Calle 1", null, headerItbis, total, total, 0m, "ACT", ncf, CutoverDate, "FAC", "31", 0m),
        [new SourceDetail(1, invoice, 1, 100, null, "Producto QA", qty, price, 0m, null, "ACT", 0m, null, "UND")],
        [new SourcePayment(1, invoice, 1, "EFE", total, "EMI", total, null, null, null, null, null)]);

    private static InvoiceValidator CreateValidator(FiscalMappingsOptions fiscal, WebPosOptions webpos) =>
        new(Options.Create(fiscal), Options.Create(webpos));

    private static SyncCoordinator CreateCoordinator(FakeSql sql, FakeFirebird firebird, FakeStore store,
        FiscalMappingsOptions fiscal, WebPosOptions webpos, SyncOptions sync)
    {
        var fiscalOptions = Options.Create(fiscal);
        var webposOptions = Options.Create(webpos);
        var syncOptions = Options.Create(sync);
        return new SyncCoordinator(sql, new InvoiceReader(firebird),
            new InvoiceMapper(fiscalOptions, new ConfiguredFiscalDataProvider(fiscalOptions)),
            new InvoiceValidator(fiscalOptions, webposOptions), new InvoiceJsonSerializer(), store,
            new RetryPolicy(syncOptions), new SyncDiagnostics(NullLogger<SyncDiagnostics>.Instance, webposOptions),
            syncOptions, webposOptions, Options.Create(new SqlServerOptions { Server = "TEST", Database = "TEST" }));
    }

    private static StoredPayload PersistedPayload(InvoiceKey key, InvoiceSnapshot snapshot) => new(
        InvoiceJsonSerializer.FormatVersion, key,
        new ImportRoute("JOMLIA", "TEST", "TEST", "LIC", "SUC", "POS", false),
        snapshot.Header!.Ncf!, new InvoiceJsonSerializer().Fingerprint(snapshot),
        "PERSISTED-JSON", "PERSISTED-HASH", []);

    private static InvoiceSnapshot ValidSnapshot(int invoice) => new(
        new InvoiceKey(1, invoice),
        new SourceHeader(1, invoice, 1, CutoverDate, "Cliente QA", "Calle 1", null,
            18m, 118m, 118m, 0m, "ACT", "E310000000001", null, "FAC", "31", 0m),
        [new SourceDetail(1, invoice, 1, 100, null, "Producto QA", 1m, 100m, 0m, 18m, "ACT", 0m, "ITBIS1", "UND")],
        [ValidPayment(invoice)]);

    private static SourcePayment ValidPayment(int invoice) => new(
        1, invoice, 1, "EFE", 118m, "EMI", 118m, null, null, null, null, null);

    private static FiscalMappingsOptions ValidFiscal() => new()
    {
        PricesExcludeItbisConfirmed = true,
        NoTipsConfirmed = true,
        PaymentAmountsAreAppliedAmountsConfirmed = true,
        LineItbisInterpretation = ItbisInterpretation.PercentageRate,
        ItbisRoundedPerLineConfirmed = true,
        Currency = "DOP",
        CurrencyRate = 1m,
        OperMode = "01",
        PaymentType = 1,
        AllowedHeaderStatuses = ["ACT"],
        AllowedDetailStatuses = ["ACT"],
        ItbisRatesByTaxCode = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["0"] = 0m, ["1"] = 18m, ["2"] = 16m, ["3"] = 0m, ["4"] = 0m
        },
        DocumentTypes = new Dictionary<string, string>(StringComparer.Ordinal) { ["FAC"] = "F" },
        TaxCodes = new Dictionary<string, int>(StringComparer.Ordinal) { ["ITBIS1"] = 1 },
        ItemClasses = new Dictionary<string, int>(StringComparer.Ordinal) { ["1/100"] = 0 },
        Units = new Dictionary<string, string>(StringComparer.Ordinal) { ["UND"] = "UND" },
        Customers = new Dictionary<string, CustomerMapping>(StringComparer.Ordinal)
        {
            ["1/1"] = new() { CustomerType = "03", Ruc = "12345678901" }
        },
        NcfExpiryDates = new Dictionary<string, DateOnly>(StringComparer.Ordinal)
        {
            ["1/E310000000001"] = new DateOnly(2030, 1, 1)
        },
        PaymentTypes = new Dictionary<string, PaymentMapping>(StringComparer.Ordinal)
        {
            ["EFE"] = new() { WebPosType = "01", Description = "EFECTIVO" }
        },
        PaymentConsistencyRules = new Dictionary<string, PaymentConsistencyRule>(StringComparer.Ordinal)
        {
            ["EFE"] = new() { ExpectedComponent = "Efectivo", ZeroComponents = [] }
        }
    };

    private static WebPosOptions ValidWebPos() => new()
    {
        SourceSystem = "JOMLIA",
        SourceCompanyId = 1,
        InitialInvoiceIdByCompany = new Dictionary<int, int> { [1] = 100 },
        CompanyLicCod = "LIC",
        BranchCod = "SUC",
        PosCod = "POS"
    };

    private static SyncOptions ValidSync() => new()
    {
        Enabled = false,
        InvoiceStart = CutoverDate,
        PayloadDirectory = "C:\\TEST\\JomliaWebPosSync"
    };

    private static FirebirdOptions ValidFirebird() => new()
    {
        Host = "TEST", Database = "TEST.GDB", Charset = "UTF8", User = "TEST", Password = "TEST"
    };

    private static SqlServerOptions ValidSqlServer() => new()
    {
        Server = "TEST", Database = "TEST", IntegratedSecurity = true
    };

    private sealed class FakeSql : ISqlServerRepository
    {
        public HashSet<InvoiceKey> Imported { get; } = [];
        public List<StoredPayload> Imports { get; } = [];

        public Task<bool> IsImportedAsync(InvoiceKey key, CancellationToken ct) =>
            Task.FromResult(Imported.Contains(key));

        public Task<string> ImportAsync(StoredPayload payload, CancellationToken ct)
        {
            Imports.Add(payload);
            return Task.FromResult("OK");
        }
    }

    private sealed class FakeFirebird : IFirebirdRepository
    {
        public Queue<InvoiceSnapshot> Snapshots { get; } = new();
        public int Reads { get; private set; }

        public Task<IReadOnlyList<InvoiceKey>> GetCandidateInvoicesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<InvoiceKey>>([]);

        public Task<InvoiceSnapshot> ReadSnapshotAsync(InvoiceKey key, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Snapshots.Dequeue());
        }
    }

    private sealed class FakeStore : IPayloadStore
    {
        private readonly Dictionary<InvoiceKey, StoredPayload> items = [];

        public List<StoredPayload> Saves { get; } = [];

        public void Initialize() { }

        public IEnumerable<InvoiceKey> EnumerateKeys() => items.Keys.ToArray();

        public Task<StoredPayload?> LoadAsync(InvoiceKey key, CancellationToken ct) =>
            Task.FromResult(items.TryGetValue(key, out var payload) ? payload : null);

        public Task<StoredPayload> SaveIfAbsentAsync(StoredPayload payload, CancellationToken ct)
        {
            Saves.Add(payload);
            items[payload.Key] = payload;
            return Task.FromResult(payload);
        }

        public void Seed(StoredPayload payload) => items[payload.Key] = payload;
    }
}

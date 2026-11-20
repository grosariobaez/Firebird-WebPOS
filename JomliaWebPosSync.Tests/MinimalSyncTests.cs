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
        var cutover = InvoiceCutover.From(ValidWebPos(), ValidSync());
        var key = new InvoiceKey(1, 100);
        Assert.False(cutover.Contains(new InvoiceKey(1, 99), CutoverDate));
        Assert.True(cutover.Contains(key, CutoverDate));
        Assert.False(cutover.Contains(key, CutoverDate.AddTicks(-1)));
        Assert.True(cutover.Contains(new InvoiceKey(1, 101), CutoverDate));
        Assert.False(cutover.Contains(new InvoiceKey(2, 100), CutoverDate));
    }

    [Fact]
    public void Emission_RequiresDetailsEmiPaymentsAndPenStatus()
    {
        var validator = CreateValidator();
        validator.ValidateSource(ValidSnapshot(100, "E310000000100"));

        Assert.Equal("SOURCE_INVOICE_CANCELLED", Assert.Throws<SyncException>(() => validator.ValidateSource(ValidSnapshot(100, "E310000000100") with { Header = ValidSnapshot(100, "E310000000100").Header! with { Status = "ANU" } })).Code);
        Assert.Equal("UNCONFIRMED_STATUS", Assert.Throws<SyncException>(() => validator.ValidateSource(ValidSnapshot(100, "E310000000100") with { Header = ValidSnapshot(100, "E310000000100").Header! with { Status = "EMI" } })).Code);
        Assert.Equal("MISSING_DETAILS", Assert.Throws<SyncException>(() => validator.ValidateSource(ValidSnapshot(100, "E310000000100") with { Details = [] })).Code);
        var partial = Assert.Throws<SyncException>(() => validator.ValidateSource(ValidSnapshot(100, "E310000000100") with { Payments = [CreditPayment(100, 118m) with { Status = "PEN" }] }));
        Assert.Equal("PARTIAL_EMISSION", partial.Code);
        Assert.True(partial.Transient);
    }

    [Fact]
    public async Task IncompleteInvoice_IsRecheckedWithoutPermanentCursor()
    {
        var firebird = new FakeFirebird();
        firebird.Snapshots.Enqueue(ValidSnapshot(100, "E310000000100") with { Payments = [CreditPayment(100, 118m) with { Status = "PEN" }] });
        firebird.Snapshots.Enqueue(ValidSnapshot(100, "E310000000100"));
        var validator = CreateValidator();

        var first = await firebird.ReadSnapshotAsync(new InvoiceKey(1, 100), CancellationToken.None);
        Assert.True(Assert.Throws<SyncException>(() => validator.ValidateSource(first)).Transient);
        var second = await firebird.ReadSnapshotAsync(new InvoiceKey(1, 100), CancellationToken.None);
        validator.ValidateSource(second);
        Assert.Equal(2, firebird.Reads);
    }

    [Fact]
    public async Task DuplicateInSourceMap_DoesNotReadOrImport()
    {
        var key = new InvoiceKey(1, 100);
        var sql = new FakeSql(); sql.Imported.Add(key);
        var firebird = new FakeFirebird();
        var store = new FakeStore();
        var coordinator = CreateCoordinator(sql, firebird, store);
        bool result = await coordinator.ProcessAsync(key, CancellationToken.None);
        Assert.False(result);
        Assert.Empty(sql.Imports);
        Assert.Equal(0, firebird.Reads);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task PersistedPayload_IsReused_AndIdempotent_WithoutDuplicateImport()
    {
        var key = new InvoiceKey(1, 572);
        var snapshot = Invoice572();
        var payload = PersistedPayload(key, snapshot);

        var sqlA = new FakeSql();
        var firebirdA = new FakeFirebird(); firebirdA.Snapshots.Enqueue(snapshot);
        var storeA = new FakeStore(); storeA.Seed(payload);
        bool reused = await CreateCoordinator(sqlA, firebirdA, storeA).ProcessAsync(key, CancellationToken.None);
        Assert.True(reused);
        Assert.Same(payload, Assert.Single(sqlA.Imports));
        Assert.Empty(storeA.Saves);

        var sqlB = new FakeSql(); sqlB.Imported.Add(key);
        var firebirdB = new FakeFirebird();
        var storeB = new FakeStore(); storeB.Seed(payload);
        bool skipped = await CreateCoordinator(sqlB, firebirdB, storeB).ProcessAsync(key, CancellationToken.None);
        Assert.False(skipped);
        Assert.Empty(sqlB.Imports);
        Assert.Equal(0, firebirdB.Reads);
    }

    [Fact]
    public async Task PersistedPayload_SourceChangeAfterPreparation_IsRejected()
    {
        var key = new InvoiceKey(1, 572);
        var snapshot = Invoice572();
        var payload = PersistedPayload(key, snapshot);
        var changed = snapshot with { Header = snapshot.Header! with { Itbis = 631m, Total = 4131m } };

        var sql = new FakeSql();
        var firebird = new FakeFirebird(); firebird.Snapshots.Enqueue(changed);
        var store = new FakeStore(); store.Seed(payload);
        bool blocked = await CreateCoordinator(sql, firebird, store).ProcessAsync(key, CancellationToken.None);
        Assert.False(blocked);
        Assert.Empty(sql.Imports);
        Assert.Empty(store.Saves);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 18)]
    [InlineData(2, 16)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    public void ItbisRate_IsCanonical(int tax, decimal expected) => Assert.Equal(expected, FiscalMappingsOptions.RequiredItbisRate(tax));

    [Fact]
    public void Configuration_RejectsNonCanonicalItbisRate()
    {
        var fiscal = new FiscalMappingsOptions();
        fiscal.ItbisRatesByTaxCode["1"] = 20m;
        var validator = new ConfigurationValidator(Options.Create(ValidFirebird()), Options.Create(ValidSqlServer()), Options.Create(ValidWebPos()), Options.Create(fiscal));
        var sync = ValidSync(); sync.Enabled = true;
        var result = validator.Validate(null, sync);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ItbisRatesByTaxCode.1", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroPriceDetails_AreExcludedFromPayload()
    {
        var snapshot = Invoice572() with
        {
            Details =
            [
                new SourceDetail(1, 572, 1, 457, null, "Producto", 1m, 3500m, 0m, null, null, 0m, null, "UND"),
                new SourceDetail(1, 572, 2, 999, null, "Cero", 1m, 0m, 0m, null, null, 0m, null, "UND")
            ]
        };
        var payload = CreateMapper().Map(snapshot);
        var detail = Assert.Single(payload.Details);
        Assert.Equal(457, detail.IdProducto);
    }

    [Fact]
    public void Invoice572_MandatoryRules_TaxExpiryPaymentCustomer()
    {
        var p572 = CreateMapper().Map(Invoice572());
        Assert.Equal(572, p572.IdFactura);
        Assert.Equal("E310000000004", p572.Header.Ncf);
        var d572 = Assert.Single(p572.Details);
        Assert.Equal(3500m, d572.Precio);
        Assert.Equal(630m, d572.Itbis);
        Assert.Equal(1, d572.Tax);
        Assert.Equal(0, d572.ItemClass);
        Assert.Equal("UND", d572.Um);
        Assert.Equal(4130m, p572.Header.Total);
        Assert.Equal(630m, p572.Header.Itbis);
        Assert.Equal(new DateOnly(2026, 12, 4), p572.Header.NcfVencimiento);
        var pay572 = Assert.Single(p572.Payments);
        Assert.Equal("F6", pay572.Tipo);
        Assert.Equal("04", GetPaymentWebPosType(p572, "F6"));
        Assert.Equal("CLIENTE 572", p572.Header.Nombre);
        Assert.Equal("CALLE 572", p572.Header.Direccion);
        Assert.Equal("00112900238", p572.Header.Ruc);
        Assert.Equal("05", p572.Header.CustomerType);
        CreateValidator().ValidatePayload(p572);
    }

    [Fact]
    public void Invoice571_PreservesUnequivocalItbisDerivation()
    {
        var p571 = CreateMapper().Map(Invoice571());
        var d571 = Assert.Single(p571.Details);
        Assert.Equal(440m, d571.Cantidad * d571.Precio);
        Assert.Equal(79.20m, d571.Itbis);
        Assert.Equal(1, d571.Tax);
        Assert.Equal(519.20m, p571.Header.Total);
        Assert.Equal(79.20m, p571.Header.Itbis);
        CreateValidator().ValidatePayload(p571);
    }

    [Fact]
    public void CreditF6_AllowsPagadoZero_WithAppliedCreditAmount()
    {
        var snapshot = Invoice572(); // FAC_PAGADO=0, FPA_CREDITO=4130
        CreateValidator().ValidateSource(snapshot);
        var payload = CreateMapper().Map(snapshot);
        Assert.Equal(0m, payload.Header.Devuelto);
        CreateValidator().ValidatePayload(payload);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DfcItbis_NullOrEmptyOrWhitespace_MapsToTax1_WithCorrectMonetaryItbis(string? taxCode)
    {
        var snapshot = Invoice572() with
        {
            Details = [Invoice572().Details[0] with { TaxCode = taxCode, Itbis = null }]
        };
        var payload = CreateMapper().Map(snapshot);
        var d = Assert.Single(payload.Details);
        Assert.Equal(1, d.Tax);
        Assert.Equal(630m, d.Itbis);
    }

    [Fact]
    public void ExplicitValidFiscalCode_IsRespected()
    {
        var snapshot = new InvoiceSnapshot(new InvoiceKey(1, 900),
            Header(900, "E310000000900", "00112900238", 0m, 100m, 0m),
            [new SourceDetail(1, 900, 1, 100, null, "Exento", 1m, 100m, 0m, 0m, null, 0m, "0", "UND")],
            [CreditPayment(900, 100m)]);
        var payload = CreateMapper().Map(snapshot);
        var d = Assert.Single(payload.Details);
        Assert.Equal(0, d.Tax);
        Assert.Equal(0m, d.Itbis);
        CreateValidator().ValidatePayload(payload);
    }

    [Fact]
    public void UnknownNonEmptyFiscalCode_IsRejected_NotConvertedTo18()
    {
        var snapshot = Invoice572() with { Details = [Invoice572().Details[0] with { TaxCode = "X9" }] };
        var ex = Assert.Throws<SyncException>(() => CreateMapper().Map(snapshot));
        Assert.Equal("UNKNOWN_FISCAL_CODE", ex.Code);
    }

    [Fact]
    public void Multiline_WithVerifiableClassification_IsAccepted()
    {
        var snapshot = new InvoiceSnapshot(new InvoiceKey(1, 576),
            Header(576, "E310000000576", "00112900238", 18m, 218m, 0m),
            [
                new SourceDetail(1, 576, 1, 100, null, "Gravado", 1m, 100m, 0m, 18m, null, 0m, "1", "UND"),
                new SourceDetail(1, 576, 2, 101, null, "Exento", 1m, 100m, 0m, 0m, null, 0m, "0", "UND")
            ],
            [CreditPayment(576, 218m)]);
        var payload = CreateMapper().Map(snapshot);
        Assert.Equal(2, payload.Details.Count);
        Assert.Equal(18m, payload.Details[0].Itbis);
        Assert.Equal(0m, payload.Details[1].Itbis);
        CreateValidator().ValidatePayload(payload);
    }

    [Fact]
    public void Multiline_FiscallyAmbiguousOrUnverifiable_IsRejected_NoProration()
    {
        // Clasificación no verificable en una línea (código desconocido no vacío) -> rechazo, sin prorrateo.
        var snapshot = new InvoiceSnapshot(new InvoiceKey(1, 574),
            Header(574, "E310000000574", "00112900238", 18m, 118m, 0m),
            [
                new SourceDetail(1, 574, 1, 100, null, "A", 1m, 50m, 0m, null, null, 0m, "X", "UND"),
                new SourceDetail(1, 574, 2, 101, null, "B", 1m, 50m, 0m, 9m, null, 0m, "1", "UND")
            ],
            [CreditPayment(574, 118m)]);
        Assert.Throws<SyncException>(() => CreateMapper().Map(snapshot));
    }

    [Fact]
    public void Medida_Und_MapsToItemClass0_AndUmUnd()
    {
        var snapshot = Invoice572() with { Details = [Invoice572().Details[0] with { Medida = " und " }] };
        var d = Assert.Single(CreateMapper().Map(snapshot).Details);
        Assert.Equal(0, d.ItemClass);
        Assert.Equal("UND", d.Um);
    }

    [Fact]
    public void Medida_Serv_IsRecognizedButUnitUnconfirmed_ThrowsSpecificError()
    {
        var snapshot = Invoice572() with { Details = [Invoice572().Details[0] with { Medida = "serv" }] };
        var ex = Assert.Throws<SyncException>(() => CreateMapper().Map(snapshot));
        Assert.Equal("SERVICE_UNIT_UNCONFIRMED", ex.Code);
        Assert.Equal("MEDIDA", ex.Field);
    }

    [Theory]
    [InlineData("131000000", "04")] // RNC 9
    [InlineData("00112900238", "05")] // Cedula 11
    public void ValidIdentifications_RncAndCedula_AreAccepted(string rawId, string expectedType)
    {
        var payload = CreateMapper().Map(Invoice572() with { Header = Invoice572().Header! with { Direccion2 = rawId } });
        Assert.Equal(expectedType, payload.Header.CustomerType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingIdentification_NonE32_IsRejected(string? direccion2)
    {
        var ex = Assert.Throws<SyncException>(() => CreateMapper().Map(Invoice572() with { Header = Invoice572().Header! with { Direccion2 = direccion2 } }));
        Assert.Equal("MISSING_CUSTOMER_IDENTIFICATION", ex.Code);
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("12345")]
    [InlineData("123456789012")]
    [InlineData("000000000")]
    public void InvalidOrConsumerIdentification_NonE32_IsRejected(string direccion2)
    {
        var ex = Assert.Throws<SyncException>(() => CreateMapper().Map(Invoice572() with { Header = Invoice572().Header! with { Direccion2 = direccion2 } }));
        Assert.True(ex.Code is "INVALID_IDENTIFICATION_FORMAT" or "MISSING_CUSTOMER_IDENTIFICATION");
    }

    [Fact]
    public void CancelledInvoice_IsRejected_AndBlockedAfterPreparation()
    {
        var cancelled = Invoice572() with { Header = Invoice572().Header! with { Status = "ANU" } };
        Assert.Equal("SOURCE_INVOICE_CANCELLED", Assert.Throws<SyncException>(() => CreateValidator().ValidateSource(cancelled)).Code);
        Assert.Equal("CANCELLED_AFTER_PREPARATION", Assert.Throws<SyncException>(() => CreateValidator().ValidateReplay(cancelled)).Code);
    }

    [Fact]
    public void NcfVencimiento_FromFacFechaVence_NonE32MissingRejected_E32Allowed()
    {
        var provider = new ConfiguredFiscalDataProvider();
        var e31 = Invoice572().Header!;
        Assert.Equal(new DateOnly(2026, 12, 4), provider.ResolveNcfExpiry(e31));
        var e31NoDate = e31 with { FechaVence = null };
        Assert.Equal("MISSING_NCF_EXPIRY", Assert.Throws<SyncException>(() => provider.ResolveNcfExpiry(e31NoDate)).Code);
        var e32 = e31 with { Ncf = "E320000000001", TipoNcf = "32", FechaVence = null };
        Assert.Null(provider.ResolveNcfExpiry(e32));
    }

    [Fact]
    public void States_Pen_NullDetailStatus_EmiPayment_Accepted()
    {
        var snapshot = Invoice572();
        Assert.Equal("PEN", snapshot.Header!.Status);
        Assert.All(snapshot.Details, d => Assert.Null(d.Status));
        Assert.All(snapshot.Payments, p => Assert.Equal("EMI", p.Status));
        CreateValidator().ValidateSource(snapshot);
    }

    [Fact]
    public void Configuration_DisabledRequiresNoProductionValues()
    {
        var validator = new ConfigurationValidator(Options.Create(new FirebirdOptions()), Options.Create(new SqlServerOptions()), Options.Create(new WebPosOptions()), Options.Create(new FiscalMappingsOptions()));
        Assert.True(validator.Validate(null, new SyncOptions { Enabled = false }).Succeeded);
    }

    [Fact]
    public void Configuration_EnabledRequiresCutoverAndDestination()
    {
        var validator = new ConfigurationValidator(Options.Create(ValidFirebird()), Options.Create(ValidSqlServer()), Options.Create(new WebPosOptions { SourceSystem = "JOMLIA" }), Options.Create(new FiscalMappingsOptions()));
        var result = validator.Validate(null, new SyncOptions { Enabled = true });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Sync:InvoiceStart", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("WebPos:InitialInvoiceIdByCompany", StringComparison.Ordinal));
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

    private static string GetPaymentWebPosType(InvoicePayload payload, string sourceType)
    {
        var exp = CreateMapper().GetPaymentExpectations(payload).Single(x => x.SourceType == sourceType);
        return exp.TargetType;
    }

    private static InvoiceValidator CreateValidator() => new(Options.Create(new FiscalMappingsOptions()), Options.Create(ValidWebPos()));
    private static InvoiceMapper CreateMapper() => new(Options.Create(new FiscalMappingsOptions()), new ConfiguredFiscalDataProvider());

    private static SyncCoordinator CreateCoordinator(FakeSql sql, FakeFirebird firebird, FakeStore store)
    {
        var fiscal = Options.Create(new FiscalMappingsOptions());
        var web = Options.Create(ValidWebPos());
        var sync = Options.Create(ValidSync());
        return new SyncCoordinator(sql, new InvoiceReader(firebird),
            new InvoiceMapper(fiscal, new ConfiguredFiscalDataProvider()),
            new InvoiceValidator(fiscal, web), new InvoiceJsonSerializer(), store,
            new RetryPolicy(sync), new SyncDiagnostics(NullLogger<SyncDiagnostics>.Instance, web),
            sync, web, Options.Create(new SqlServerOptions { Server = "TEST", Database = "TEST" }));
    }

    private static StoredPayload PersistedPayload(InvoiceKey key, InvoiceSnapshot snapshot)
    {
        var serializer = new InvoiceJsonSerializer();
        var mapper = CreateMapper();
        var mapped = mapper.Map(snapshot);
        string json = serializer.Serialize(mapped);
        return new(InvoiceJsonSerializer.FormatVersion, key,
            new ImportRoute("JOMLIA", "TEST", "TEST", "LIC", "SUC", "POS", false),
            mapped.Header.Ncf, serializer.Fingerprint(snapshot), json,
            InvoiceJsonSerializer.HashSqlPayload(json), mapper.GetPaymentExpectations(mapped));
    }

    private static InvoiceSnapshot ValidSnapshot(int invoice, string ncf) => new(
        new InvoiceKey(1, invoice),
        Header(invoice, ncf, "00112900238", 18m, 118m, 0m),
        [new SourceDetail(1, invoice, 1, 100, null, "Producto", 1m, 100m, 0m, 18m, null, 0m, "1", "UND")],
        [CreditPayment(invoice, 118m)]);

    private static InvoiceSnapshot Invoice572() => new(
        new InvoiceKey(1, 572),
        new SourceHeader(1, 572, 29, CutoverDate, "CLIENTE 572", "CALLE 572", "001-1290023-8", 630m, 4130m, 0m, 0m, "PEN", "E310000000004", new DateTime(2026, 12, 4), "FAC", "31", 0m),
        [new SourceDetail(1, 572, 1, 457, "R001", "PRODUCTO 572", 1m, 3500m, 0m, null, null, 0m, null, "UND")],
        [CreditPayment(572, 4130m)]);

    private static InvoiceSnapshot Invoice571() => new(
        new InvoiceKey(1, 571),
        new SourceHeader(1, 571, 29, CutoverDate, "CLIENTE 571", "CALLE 571", "00112900238", 79.20m, 519.20m, 0m, 0m, "PEN", "E310000000003", new DateTime(2026, 12, 4), "FAC", "31", 0m),
        [new SourceDetail(1, 571, 1, 457, "R001", "PRODUCTO 571", 2m, 220m, 0m, null, null, 0m, null, "UND")],
        [CreditPayment(571, 519.20m)]);

    private static SourceHeader Header(int invoice, string ncf, string? direccion2, decimal itbis, decimal total, decimal pagado) =>
        new(1, invoice, 1, CutoverDate, "Cliente", "Calle 1", direccion2, itbis, total, pagado, 0m, "PEN", ncf, new DateTime(2026, 12, 31), "FAC", ncf.StartsWith("E32") ? "32" : "31", 0m);

    private static SourcePayment CreditPayment(int invoice, decimal amount) =>
        new(1, invoice, 1, "F6", amount, "EMI", 0m, 0m, 0m, 0m, amount, null);

    private static WebPosOptions ValidWebPos() => new() { SourceSystem = "JOMLIA", SourceCompanyId = 1, InitialInvoiceIdByCompany = new Dictionary<int, int> { [1] = 100 }, CompanyLicCod = "LIC", BranchCod = "SUC", PosCod = "POS" };
    private static SyncOptions ValidSync() => new() { Enabled = false, InvoiceStart = CutoverDate, PayloadDirectory = "C:\\TEST\\JomliaWebPosSync" };
    private static FirebirdOptions ValidFirebird() => new() { Host = "TEST", Database = "TEST.GDB", Charset = "UTF8", User = "TEST", Password = "TEST" };
    private static SqlServerOptions ValidSqlServer() => new() { Server = "TEST", Database = "TEST", IntegratedSecurity = true };

    private sealed class FakeSql : ISqlServerRepository
    {
        public HashSet<InvoiceKey> Imported { get; } = [];
        public List<StoredPayload> Imports { get; } = [];
        public Task<bool> IsImportedAsync(InvoiceKey key, CancellationToken ct) => Task.FromResult(Imported.Contains(key));
        public Task<string> ImportAsync(StoredPayload payload, CancellationToken ct) { Imports.Add(payload); return Task.FromResult("OK"); }
    }

    private sealed class FakeFirebird : IFirebirdRepository
    {
        public Queue<InvoiceSnapshot> Snapshots { get; } = new();
        public int Reads { get; private set; }
        public Task<IReadOnlyList<InvoiceKey>> GetCandidateInvoicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InvoiceKey>>([]);
        public Task<InvoiceSnapshot> ReadSnapshotAsync(InvoiceKey key, CancellationToken ct) { Reads++; return Task.FromResult(Snapshots.Dequeue()); }
    }

    private sealed class FakeStore : IPayloadStore
    {
        private readonly Dictionary<InvoiceKey, StoredPayload> items = [];
        public List<StoredPayload> Saves { get; } = [];
        public void Initialize() { }
        public IEnumerable<InvoiceKey> EnumerateKeys() => items.Keys.ToArray();
        public Task<StoredPayload?> LoadAsync(InvoiceKey key, CancellationToken ct) => Task.FromResult(items.TryGetValue(key, out var p) ? p : null);
        public Task<StoredPayload> SaveIfAbsentAsync(StoredPayload payload, CancellationToken ct) { Saves.Add(payload); items[payload.Key] = payload; return Task.FromResult(payload); }
        public void Seed(StoredPayload payload) => items[payload.Key] = payload;
    }
}

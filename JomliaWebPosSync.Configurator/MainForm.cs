using System.Globalization;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Configurator;

internal sealed class MainForm : Form
{
    private const string ServiceName = ConfigurationLocator.ServiceName;
    private readonly string configPath;
    private readonly string serviceDirectory;
    private readonly ProtectedSecretStore secretStore = new();
    private readonly ConnectionProbe probe = new();
    private JsonObject config = new();
    private bool loading;
    private bool connectionsValidated;
    private bool hasStoredFirebirdPassword;
    private bool hasStoredSqlPassword;
    private string pendingFiscalText = string.Empty;

    private readonly TextBox txtFbHost = new();
    private readonly NumericUpDown numFbPort = new() { Minimum = 1, Maximum = 65535, Value = 3050, Width = 120 };
    private readonly TextBox txtFbDatabase = new();
    private readonly TextBox txtFbCharset = new();
    private readonly TextBox txtFbUser = new();
    private readonly TextBox txtFbPassword = new() { UseSystemPasswordChar = true };
    private readonly Label lblFbPassword = new() { AutoSize = true, Text = "Contraseña", Margin = new Padding(0, 7, 10, 7) };
    private readonly TextBox txtFbRole = new();
    private readonly TextBox txtSqlServer = new();
    private readonly TextBox txtSqlDatabase = new();
    private readonly TextBox txtSqlUser = new();
    private readonly TextBox txtSqlPassword = new() { UseSystemPasswordChar = true };
    private readonly Label lblSqlPassword = new() { AutoSize = true, Text = "Contraseña SQL", Margin = new Padding(0, 7, 10, 7) };
    private readonly CheckBox chkSqlIntegrated = new() { Text = "Autenticación integrada de Windows", AutoSize = true };
    private readonly CheckBox chkSqlEncrypt = new() { Text = "Cifrar conexión SQL Server", AutoSize = true };
    private readonly CheckBox chkSqlTrust = new() { Text = "Permitir TrustServerCertificate=true (selección explícita)", AutoSize = true };
    private readonly Label lblTrustWarning = new()
    {
        Text = "Advertencia de seguridad: TrustServerCertificate=true omite la validación de la cadena de certificados. Habilítelo solo si es estrictamente necesario.",
        AutoSize = false,
        ForeColor = Color.DarkRed,
        Height = 34,
        Dock = DockStyle.Fill
    };
    private readonly TextBox txtLicense = new();
    private readonly TextBox txtBranch = new();
    private readonly TextBox txtPos = new();
    private readonly NumericUpDown numSourceCompanyId = new() { Minimum = 0, Maximum = 1000000, Width = 120 };
    private readonly NumericUpDown numInitialInvoiceId = new() { Minimum = 0, Maximum = int.MaxValue, Width = 160 };
    private readonly DateTimePicker dtInvoiceStart = new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true, Width = 160 };
    private readonly TextBox txtPayloadDirectory = new();
    private readonly CheckBox chkEnabled = new() { Text = "Sincronización activa", AutoSize = true };
    private readonly Label lblStatus = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 44, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
    private readonly Label lblNotice = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 34, ForeColor = Color.DarkOrange };
    private readonly Label lblTestResult = new() { AutoSize = false, Height = 42, Dock = DockStyle.Fill, Text = "Conexiones sin validar." };
    private readonly TextBox txtDetails = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 170, Dock = DockStyle.Fill, WordWrap = true };
    private readonly Button btnTest = new() { Text = "Probar Conexiones", AutoSize = true };
    private readonly Button btnSave = new() { Text = "Guardar Configuración", AutoSize = true };
    private readonly Button btnActivate = new() { Text = "Activar Sincronización", AutoSize = true, Enabled = false };
    private readonly Button btnDeactivate = new() { Text = "Desactivar Sincronización", AutoSize = true };
    private readonly Button btnRefresh = new() { Text = "Recargar / Ver Estado", AutoSize = true };
    private readonly Button btnStart = new() { Text = "Iniciar Servicio", AutoSize = true };
    private readonly Button btnStop = new() { Text = "Detener Servicio", AutoSize = true };

    public MainForm()
    {
        configPath = ConfigurationLocator.ResolveEffectiveConfigPath();
        serviceDirectory = Path.GetDirectoryName(configPath) ?? ConfigurationLocator.GetServiceCandidateDirectory(AppContext.BaseDirectory);
        Text = "IXU | Configuración JomliaWebPosSync";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 700);
        ClientSize = new Size(1040, 820);
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildUi();
        LoadConfiguration();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var header = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var lblBrand = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(980, 0),
            Text = "IXU | INNOVAXD UX SRL - Inteligencia · Ejecución · Utilidad\nTecnología para operaciones del mundo real · https://innovaxd.ca/es/",
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 4)
        };
        header.Controls.Add(lblBrand, 0, 0);
        header.Controls.Add(lblStatus, 0, 1);
        header.Controls.Add(lblNotice, 0, 2);
        root.Controls.Add(header, 0, 0);

        var contentHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0) };
        var content = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        contentHost.Controls.Add(content);
        root.Controls.Add(contentHost, 0, 1);

        content.Controls.Add(BuildFirebirdGroup(), 0, 0);
        content.Controls.Add(BuildSqlServerGroup(), 0, 1);
        content.Controls.Add(BuildSyncGroup(), 0, 2);
        content.Controls.Add(BuildValidationGroup(), 0, 3);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        actions.Controls.AddRange([btnTest, btnSave, btnActivate, btnDeactivate, btnRefresh, btnStart, btnStop]);
        root.Controls.Add(actions, 0, 2);

        btnTest.Click += async (_, _) => await TestConnectionsAsync();
        btnSave.Click += (_, _) => SaveConfiguration(chkEnabled.Checked, false);
        btnActivate.Click += (_, _) => ActivateSync();
        btnDeactivate.Click += (_, _) => DeactivateSync();
        btnRefresh.Click += (_, _) => LoadConfiguration();
        btnStart.Click += (_, _) => ChangeService(true);
        btnStop.Click += (_, _) => ChangeService(false);
        chkSqlIntegrated.CheckedChanged += (_, _) => { UpdateSqlCredentialState(); MarkDirty(); };
        chkSqlTrust.CheckedChanged += (_, _) => { lblTrustWarning.Visible = chkSqlTrust.Checked; MarkDirty(); };

        foreach (var control in new Control[]
        {
            txtFbHost, numFbPort, txtFbDatabase, txtFbCharset, txtFbUser, txtFbPassword, txtFbRole,
            txtSqlServer, txtSqlDatabase, txtSqlUser, txtSqlPassword, chkSqlEncrypt, chkSqlTrust,
            txtLicense, txtBranch, txtPos, numSourceCompanyId, numInitialInvoiceId, dtInvoiceStart,
            txtPayloadDirectory, chkEnabled
        })
            WireDirty(control);

        lblTrustWarning.Visible = chkSqlTrust.Checked;
        Shown += (_, _) => contentHost.AutoScrollPosition = new Point(0, 0);
    }

    private GroupBox BuildFirebirdGroup()
    {
        var table = CreateTable();
        AddRow(table, "Host Firebird", txtFbHost);
        AddRow(table, "Puerto", numFbPort);
        AddRow(table, "Base de datos", txtFbDatabase);
        AddRow(table, "Charset", txtFbCharset);
        AddRow(table, "Usuario", txtFbUser);
        AddRow(table, lblFbPassword, txtFbPassword);
        AddRow(table, "Rol", txtFbRole);
        return CreateGroup("Firebird - Origen", table);
    }

    private GroupBox BuildSqlServerGroup()
    {
        var table = CreateTable();
        AddRow(table, "Servidor", txtSqlServer);
        AddRow(table, "Base de datos", txtSqlDatabase);
        AddRow(table, "", chkSqlIntegrated);
        AddRow(table, "", chkSqlEncrypt);
        AddRow(table, "", chkSqlTrust);
        AddSpanning(table, lblTrustWarning);
        AddRow(table, "Usuario SQL", txtSqlUser);
        AddRow(table, lblSqlPassword, txtSqlPassword);
        return CreateGroup("SQL Server - Destino", table);
    }

    private GroupBox BuildSyncGroup()
    {
        var table = CreateTable();
        AddRow(table, "CompanyLicCod", txtLicense);
        AddRow(table, "BranchCod", txtBranch);
        AddRow(table, "PosCod", txtPos);
        AddRow(table, "SourceCompanyId", numSourceCompanyId);
        AddRow(table, "IDFACTURA mínimo a sincronizar (inclusive)", numInitialInvoiceId);
        AddRow(table, "InvoiceStart (YYYY-MM-DD)", dtInvoiceStart);
        AddRow(table, "Carpeta de payloads", txtPayloadDirectory);
        AddRow(table, "", chkEnabled);
        return CreateGroup("WebPOS y sincronización", table);
    }

    private GroupBox BuildValidationGroup()
    {
        var table = CreateTable();
        AddSpanning(table, lblTestResult);
        AddRow(table, "Detalle", txtDetails);
        return CreateGroup("Verificación", table);
    }

    private static TableLayoutPanel CreateTable()
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static GroupBox CreateGroup(string title, TableLayoutPanel table) => new()
    {
        Text = title,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        Padding = new Padding(10),
        Margin = new Padding(0, 0, 0, 10),
        Controls = { table }
    };

    private static void AddRow(TableLayoutPanel table, string label, Control control) =>
        AddRow(table, new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 10, 7), Anchor = AnchorStyles.Left }, control);

    private static void AddRow(TableLayoutPanel table, Control label, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(label, 0, row);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, 4, 0, 4);
        if (control is NumericUpDown or DateTimePicker or CheckBox)
            control.Anchor = AnchorStyles.Left;
        table.Controls.Add(control, 1, row);
    }

    private static void AddSpanning(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 4, 0, 4);
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private void WireDirty(Control control)
    {
        switch (control)
        {
            case TextBox textBox: textBox.TextChanged += (_, _) => MarkDirty(); break;
            case CheckBox checkBox: checkBox.CheckedChanged += (_, _) => MarkDirty(); break;
            case DateTimePicker picker: picker.ValueChanged += (_, _) => MarkDirty(); break;
            case NumericUpDown numeric: numeric.ValueChanged += (_, _) => MarkDirty(); break;
        }
    }

    private void LoadConfiguration()
    {
        loading = true;
        try
        {
            config = JsonConfigurationEditor.LoadOrCreate(configPath);
            IxuVerifiedDefaults.ApplyMissing(config);
            var firebird = JsonConfigurationEditor.Section(config, "Firebird");
            var sql = JsonConfigurationEditor.Section(config, "SqlServer");
            var webPos = JsonConfigurationEditor.Section(config, "WebPos");
            var sync = JsonConfigurationEditor.Section(config, "Sync");

            txtFbHost.Text = JsonConfigurationEditor.GetString(firebird, "Host");
            numFbPort.Value = Clamp(JsonConfigurationEditor.GetInt(firebird, "Port", 3050), (int)numFbPort.Minimum, (int)numFbPort.Maximum);
            txtFbDatabase.Text = JsonConfigurationEditor.GetString(firebird, "Database");
            txtFbCharset.Text = JsonConfigurationEditor.GetString(firebird, "Charset");
            txtFbUser.Text = JsonConfigurationEditor.GetString(firebird, "User");
            txtFbRole.Text = JsonConfigurationEditor.GetString(firebird, "Role");
            hasStoredFirebirdPassword = !string.IsNullOrEmpty(secretStore.Read(ProtectedSecretStore.FirebirdPasswordKey));
            txtFbPassword.Text = string.Empty;

            txtSqlServer.Text = JsonConfigurationEditor.GetString(sql, "Server");
            txtSqlDatabase.Text = JsonConfigurationEditor.GetString(sql, "Database");
            chkSqlIntegrated.Checked = JsonConfigurationEditor.GetBool(sql, "IntegratedSecurity", true);
            chkSqlEncrypt.Checked = JsonConfigurationEditor.GetBool(sql, "Encrypt", true);
            chkSqlTrust.Checked = JsonConfigurationEditor.GetBool(sql, "TrustServerCertificate", false);
            txtSqlUser.Text = JsonConfigurationEditor.GetString(sql, "User");
            hasStoredSqlPassword = !string.IsNullOrEmpty(secretStore.Read(ProtectedSecretStore.SqlServerPasswordKey));
            txtSqlPassword.Text = string.Empty;

            txtLicense.Text = JsonConfigurationEditor.GetString(webPos, "CompanyLicCod");
            txtBranch.Text = JsonConfigurationEditor.GetString(webPos, "BranchCod");
            txtPos.Text = JsonConfigurationEditor.GetString(webPos, "PosCod");
            var companyId = JsonConfigurationEditor.GetNullableInt(webPos, "SourceCompanyId");
            numSourceCompanyId.Value = companyId.HasValue ? Clamp(companyId.Value, (int)numSourceCompanyId.Minimum, (int)numSourceCompanyId.Maximum) : 0;
            var initialId = 0;
            if (companyId is int id && webPos["InitialInvoiceIdByCompany"] is JsonObject initialMap)
            {
                var key = id.ToString(CultureInfo.InvariantCulture);
                if (initialMap[key] is JsonValue value && value.TryGetValue<int>(out var invoiceId)) initialId = invoiceId;
            }
            numInitialInvoiceId.Value = initialId > 0 ? Clamp(initialId, (int)numInitialInvoiceId.Minimum, (int)numInitialInvoiceId.Maximum) : 0;

            var start = JsonConfigurationEditor.GetString(sync, "InvoiceStart");
            if (DateTime.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                dtInvoiceStart.Value = date;
                dtInvoiceStart.Checked = true;
            }
            else
            {
                dtInvoiceStart.Value = DateTime.Today;
                dtInvoiceStart.Checked = false;
            }

            txtPayloadDirectory.Text = JsonConfigurationEditor.GetString(sync, "PayloadDirectory", @"C:\ProgramData\PuntoAparte\JomliaWebPosSync\Outbox");
            chkEnabled.Checked = JsonConfigurationEditor.GetBool(sync, "Enabled", false);
            UpdateSqlCredentialState();
            UpdateSecretLabels();
            lblTestResult.Text = File.Exists(configPath)
                ? "Configuración cargada. Las credenciales protegidas no se muestran; probá las conexiones si realizás cambios."
                : "Instalación nueva o configuración no encontrada. Completá los valores indispensables y guardá.";
            lblTestResult.ForeColor = SystemColors.ControlText;
            txtDetails.Clear();
            connectionsValidated = false;
            btnActivate.Enabled = false;
            lblNotice.Text = BuildNotice();
            ShowPendingFiscalItems();
        }
        catch (Exception ex)
        {
            config = new JsonObject();
            lblNotice.Text = "No se pudo cargar la configuración existente.";
            MessageBox.Show(this, $"No se pudo cargar la configuración:\n{ex.Message}", "Configuración", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            loading = false;
            RefreshStatus();
        }
    }

    private void ShowPendingFiscalItems()
    {
        var pending = IxuVerifiedDefaults.GetPendingFiscalItems(config);
        if (pending.Count == 0)
        {
            pendingFiscalText = "Reglas fiscales verificadas precargadas. No hay reglas pendientes de validación.";
            txtDetails.Text = pendingFiscalText;
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine("Reglas fiscales verificadas precargadas (solo faltantes; no se sobrescriben valores existentes).");
        sb.AppendLine("Pendientes de validación (no inventadas):");
        foreach (var item in pending) sb.AppendLine("  - " + item);
        sb.AppendLine();
        sb.Append("Valores dependientes del cliente (conexiones, credenciales, licencia/sucursal/POS, compañía y corte) deben completarse por el usuario.");
        pendingFiscalText = sb.ToString();
        txtDetails.Text = pendingFiscalText;
    }

    private string BuildNotice()
    {
        var notices = new List<string>();
        var alternate = Path.Combine(AppContext.BaseDirectory, ConfigurationLocator.ConfigFileName);
        if (!PathsEqual(alternate, configPath) && File.Exists(alternate))
            notices.Add($"Advertencia: existe otro appsettings.json en {alternate}. El servicio utiliza la ruta efectiva mostrada.");
        var overrideNotice = BuildOverrideNotice();
        if (!string.IsNullOrWhiteSpace(overrideNotice)) notices.Add(overrideNotice);
        return notices.Count == 0 ? "Configuración efectiva del servicio cargada." : string.Join(" ", notices);
    }

    private string? BuildOverrideNotice()
    {
        var notices = new List<string>();
        if (File.Exists(Path.Combine(serviceDirectory, ConfigurationLocator.LocalConfigFileName)))
            notices.Add("Advertencia: appsettings.Local.json puede sobreescribir valores efectivos del servicio.");
        if (Environment.GetEnvironmentVariables().Keys.OfType<string>().Any(key => key.StartsWith("JOMLIA_SYNC_", StringComparison.OrdinalIgnoreCase)))
            notices.Add("Advertencia: variables de entorno JOMLIA_SYNC_ pueden sobreescribir valores efectivos; verifique el valor efectivo real.");
        return notices.Count == 0 ? null : string.Join(" ", notices);
    }

    private void UpdateSqlCredentialState()
    {
        var enabled = !chkSqlIntegrated.Checked;
        txtSqlUser.Enabled = enabled;
        txtSqlPassword.Enabled = enabled;
        lblSqlPassword.Enabled = enabled;
    }

    private void UpdateSecretLabels()
    {
        lblFbPassword.Text = hasStoredFirebirdPassword ? "Contraseña (protegida)" : "Contraseña";
        lblSqlPassword.Text = hasStoredSqlPassword ? "Contraseña SQL (protegida)" : "Contraseña SQL";
    }

    private void MarkDirty()
    {
        if (loading) return;
        connectionsValidated = false;
        btnActivate.Enabled = false;
        lblTestResult.Text = "Hay cambios sin validar. Probá las conexiones nuevamente.";
        lblTestResult.ForeColor = Color.DarkOrange;
    }

    private async Task TestConnectionsAsync()
    {
        SetBusy(true);
        try
        {
            var firebird = BuildFirebirdOptionsForTest();
            var sql = BuildSqlOptionsForTest();
            var results = await Task.WhenAll(probe.TestFirebirdAsync(firebird), probe.TestSqlServerAsync(sql));

            var lines = new List<string>();
            foreach (var result in results)
            {
                lines.Add(result.Message);
                lines.AddRange(result.Details.Select(detail => "  - " + detail));
            }

            var connectionOk = results.All(result => result.Ok);
            connectionsValidated = connectionOk;
            if (connectionOk)
            {
                var errors = ValidateConfiguration();
                if (errors.Count == 0)
                {
                    lblTestResult.Text = "Conexiones y configuración válidas. Podés guardar y activar con autorización explícita.";
                    lblTestResult.ForeColor = Color.DarkGreen;
                    btnActivate.Enabled = true;
                }
                else
                {
                    lines.Add(string.Empty);
                    lines.Add("Configuración incompleta:");
                    lines.AddRange(errors.Select(error => "  - " + error));
                    lblTestResult.Text = "Conexiones correctas, pero la configuración aún no permite activar.";
                    lblTestResult.ForeColor = Color.DarkOrange;
                    btnActivate.Enabled = false;
                }
            }
            else
            {
                lblTestResult.Text = "Falló al menos una conexión. Revisá el detalle.";
                lblTestResult.ForeColor = Color.DarkRed;
                btnActivate.Enabled = false;
            }

            var detailsText = string.Join(Environment.NewLine, lines);
            if (!string.IsNullOrWhiteSpace(pendingFiscalText))
                detailsText += Environment.NewLine + Environment.NewLine + pendingFiscalText;
            txtDetails.Text = detailsText;
        }
        catch (Exception ex)
        {
            connectionsValidated = false;
            btnActivate.Enabled = false;
            lblTestResult.Text = "La prueba de conexiones falló.";
            lblTestResult.ForeColor = Color.DarkRed;
            txtDetails.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private List<string> ValidateConfiguration()
    {
        var clone = (JsonObject)config.DeepClone();
        ApplyUiTo(clone, chkEnabled.Checked);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        JsonConfigurationEditor.Flatten(clone, string.Empty, values);
        values["Firebird:Password"] = SecretUpdatePlanner.ResolveForTest(txtFbPassword.Text, ProtectedSecretStore.FirebirdPasswordKey, secretStore);
        values["SqlServer:Password"] = chkSqlIntegrated.Checked
            ? string.Empty
            : SecretUpdatePlanner.ResolveForTest(txtSqlPassword.Text, ProtectedSecretStore.SqlServerPasswordKey, secretStore);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var firebird = configuration.GetSection("Firebird").Get<FirebirdOptions>() ?? new FirebirdOptions();
        var sql = configuration.GetSection("SqlServer").Get<SqlServerOptions>() ?? new SqlServerOptions();
        var webPos = configuration.GetSection("WebPos").Get<WebPosOptions>() ?? new WebPosOptions();
        var fiscal = configuration.GetSection("FiscalMappings").Get<FiscalMappingsOptions>() ?? new FiscalMappingsOptions();
        var sync = configuration.GetSection("Sync").Get<SyncOptions>() ?? new SyncOptions { Enabled = true };

        var validator = new ConfigurationValidator(Options.Create(firebird), Options.Create(sql), Options.Create(webPos), Options.Create(fiscal));
        var result = validator.Validate(null, sync);
        return result.Failed ? result.Failures?.ToList() ?? ["Configuración inválida."] : [];
    }

    private bool SaveConfiguration(bool enabled, bool restartService)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var target = (JsonObject)config.DeepClone();
            IxuVerifiedDefaults.ApplyMissing(target);
            ApplyUiTo(target, enabled);
            var json = target.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var temporary = configPath + ".tmp." + Guid.NewGuid().ToString("N");
            var backup = configPath + ".bak";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(configPath)) File.Replace(temporary, configPath, backup, true);
            else File.Move(temporary, configPath);
            if (File.Exists(temporary)) File.Delete(temporary);

            var secretUpdates = SecretUpdatePlanner.BuildUpdates(txtFbPassword.Text, txtSqlPassword.Text, chkSqlIntegrated.Checked, secretStore);
            if (secretUpdates.Count > 0) secretStore.Write(secretUpdates);

            config = target;
            chkEnabled.Checked = enabled;
            txtFbPassword.Text = string.Empty;
            txtSqlPassword.Text = string.Empty;
            hasStoredFirebirdPassword = !string.IsNullOrEmpty(secretStore.Read(ProtectedSecretStore.FirebirdPasswordKey));
            hasStoredSqlPassword = !string.IsNullOrEmpty(secretStore.Read(ProtectedSecretStore.SqlServerPasswordKey));
            UpdateSecretLabels();
            if (restartService) RestartService();
            var overrideNotice = BuildOverrideNotice();
            lblTestResult.Text = enabled
                ? "Configuración guardada. Sincronización activa solo por autorización explícita."
                : "Configuración guardada. La sincronización permanece inactiva.";
            if (!string.IsNullOrWhiteSpace(overrideNotice))
                lblTestResult.Text += " " + overrideNotice;
            lblTestResult.ForeColor = string.IsNullOrWhiteSpace(overrideNotice) ? Color.DarkGreen : Color.DarkOrange;
            RefreshStatus();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo guardar la configuración:\n{ex.Message}", "Guardar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void ActivateSync()
    {
        if (!connectionsValidated)
        {
            MessageBox.Show(this, "Primero probá las conexiones y corregí cualquier error.", "Activar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var errors = ValidateConfiguration();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, "La configuración no es válida:\n\n" + string.Join("\n", errors), "Activar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (SaveConfiguration(true, true))
        {
            connectionsValidated = false;
            btnActivate.Enabled = false;
        }
    }

    private void DeactivateSync()
    {
        if (SaveConfiguration(false, true))
        {
            connectionsValidated = false;
            btnActivate.Enabled = false;
        }
    }

    private void ChangeService(bool start)
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            service.Refresh();
            if (start && service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            else if (!start && service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo cambiar el estado del servicio:\n{ex.Message}", "Servicio", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshStatus();
    }

    private void RestartService()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            service.Refresh();
            if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"La configuración se guardó, pero no se pudo reiniciar el servicio:\n{ex.Message}", "Servicio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshStatus()
    {
        var enabled = ReadEnabledFromFile();
        string serviceStatus;
        try
        {
            using var service = new ServiceController(ServiceName);
            serviceStatus = service.Status.ToString();
        }
        catch (InvalidOperationException) { serviceStatus = "no instalado"; }
        catch { serviceStatus = "desconocido"; }
        lblStatus.Text = $"Servicio: {serviceStatus}  |  Sincronización: {(enabled ? "ACTIVA" : "INACTIVA")}\nConfiguración efectiva: {configPath}";
    }

    private bool ReadEnabledFromFile()
    {
        try
        {
            if (!File.Exists(configPath)) return false;
            var node = JsonNode.Parse(File.ReadAllText(configPath), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return node?["Sync"]?["Enabled"] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;
        }
        catch { return false; }
    }

    private void SetBusy(bool busy)
    {
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        btnTest.Enabled = !busy;
        btnSave.Enabled = !busy;
        btnActivate.Enabled = !busy && connectionsValidated;
        btnDeactivate.Enabled = !busy;
        btnRefresh.Enabled = !busy;
        btnStart.Enabled = !busy;
        btnStop.Enabled = !busy;
    }

    private FirebirdOptions BuildFirebirdOptionsForTest() => new()
    {
        Host = txtFbHost.Text.Trim(),
        Port = (int)numFbPort.Value,
        Database = txtFbDatabase.Text.Trim(),
        Charset = txtFbCharset.Text.Trim(),
        User = txtFbUser.Text.Trim(),
        Password = SecretUpdatePlanner.ResolveForTest(txtFbPassword.Text, ProtectedSecretStore.FirebirdPasswordKey, secretStore),
        Role = txtFbRole.Text.Trim()
    };

    private SqlServerOptions BuildSqlOptionsForTest() => new()
    {
        Server = txtSqlServer.Text.Trim(),
        Database = txtSqlDatabase.Text.Trim(),
        IntegratedSecurity = chkSqlIntegrated.Checked,
        Encrypt = chkSqlEncrypt.Checked,
        TrustServerCertificate = chkSqlTrust.Checked,
        User = txtSqlUser.Text.Trim(),
        Password = chkSqlIntegrated.Checked ? string.Empty : SecretUpdatePlanner.ResolveForTest(txtSqlPassword.Text, ProtectedSecretStore.SqlServerPasswordKey, secretStore)
    };

    private void ApplyUiTo(JsonObject target, bool enabled)
    {
        JsonConfigurationEditor.Set(target, "Firebird", "Host", txtFbHost.Text.Trim());
        JsonConfigurationEditor.Set(target, "Firebird", "Port", (int)numFbPort.Value);
        JsonConfigurationEditor.Set(target, "Firebird", "Database", txtFbDatabase.Text.Trim());
        JsonConfigurationEditor.Set(target, "Firebird", "Charset", txtFbCharset.Text.Trim());
        JsonConfigurationEditor.Set(target, "Firebird", "User", txtFbUser.Text.Trim());
        JsonConfigurationEditor.Set(target, "Firebird", "Password", string.Empty);
        JsonConfigurationEditor.Set(target, "Firebird", "Role", txtFbRole.Text.Trim());

        JsonConfigurationEditor.Set(target, "SqlServer", "Server", txtSqlServer.Text.Trim());
        JsonConfigurationEditor.Set(target, "SqlServer", "Database", txtSqlDatabase.Text.Trim());
        JsonConfigurationEditor.Set(target, "SqlServer", "IntegratedSecurity", chkSqlIntegrated.Checked);
        JsonConfigurationEditor.Set(target, "SqlServer", "Encrypt", chkSqlEncrypt.Checked);
        JsonConfigurationEditor.Set(target, "SqlServer", "TrustServerCertificate", chkSqlTrust.Checked);
        JsonConfigurationEditor.Set(target, "SqlServer", "User", txtSqlUser.Text.Trim());
        JsonConfigurationEditor.Set(target, "SqlServer", "Password", string.Empty);

        JsonConfigurationEditor.Set(target, "WebPos", "SourceSystem", "JOMLIA");
        JsonConfigurationEditor.Set(target, "WebPos", "CompanyLicCod", txtLicense.Text.Trim());
        JsonConfigurationEditor.Set(target, "WebPos", "BranchCod", txtBranch.Text.Trim());
        JsonConfigurationEditor.Set(target, "WebPos", "PosCod", txtPos.Text.Trim());
        var companyId = numSourceCompanyId.Value > 0 ? (int)numSourceCompanyId.Value : (int?)null;
        JsonConfigurationEditor.Set(target, "WebPos", "SourceCompanyId", companyId is int companyValue ? JsonValue.Create(companyValue) : null);
        var initialInvoiceId = numInitialInvoiceId.Value > 0 ? (int)numInitialInvoiceId.Value : (int?)null;
        JsonConfigurationEditor.MergeCompanyInitialInvoiceId(JsonConfigurationEditor.Section(target, "WebPos"), companyId, initialInvoiceId);

        JsonConfigurationEditor.Set(target, "Sync", "Enabled", enabled);
        JsonConfigurationEditor.Set(target, "Sync", "InvoiceStart", dtInvoiceStart.Checked ? dtInvoiceStart.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
        JsonConfigurationEditor.Set(target, "Sync", "PayloadDirectory", txtPayloadDirectory.Text.Trim());
    }

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

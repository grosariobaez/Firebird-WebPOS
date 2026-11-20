# IXU — JomliaWebPosSync

**IXU.** Inteligencia · Ejecución · Utilidad.
Tecnología para operaciones del mundo real.
INNOVAXD UX SRL — https://innovaxd.ca/es/

Servicio Windows para **.NET 10 / Windows** que lee facturas emitidas de JOMLIA en Firebird y las entrega a WebPOS/SQL Server mediante `dbo.usp_ImportarFacturaFirebird`.

## Estado

- `Sync:Enabled=false` por defecto: la sincronización no se activa al instalar.
- No se ejecutaron conexiones a producción, activación DGII ni instalación aislada end-to-end en esta entrega.
- El instalador se genera como `installer/output/JomliaWebPosSync-Setup.exe`.

## Componentes

- `JomliaWebPosSync/`: servicio Worker, lectura Firebird, validación fiscal, payload durable, ejecución del SP e idempotencia.
- `JomliaWebPosSync.Configurator/`: configurador WinForms con prueba de conexiones, secretos protegidos y activación controlada.
- `installer/`: script Inno Setup y build reproducible del instalador.
- `docs/PRODUCTION_INSTALLATION.md`: instalación, configuración, activación, rollback y diagnóstico.

## Seguridad

- Las contraseñas no se guardan en `appsettings.json`, scripts ni logs.
- Se almacenan cifradas con DPAPI `LocalMachine` en `%ProgramData%\PuntoAparte\JomliaWebPosSync\secrets.json`, con ACL restringida a SYSTEM y Administradores.
- SQL Server usa `Encrypt=true` y `TrustServerCertificate=false` por defecto. `TrustServerCertificate=true` solo puede habilitarse explícitamente en el configurador y muestra advertencia de seguridad.
- El servicio se ejecuta como `LocalSystem` y no requiere sesión interactiva.

## Configuración y activación

Completar servidor SQL, credenciales, charset Firebird, `SourceCompanyId`, licencia/sucursal/POS, `InvoiceStart`, **IDFACTURA mínimo a sincronizar (inclusive)** (`IDFACTURA >= valor`) y carpeta durable desde el configurador. No utilizar valores de QA como predeterminados de producción.

Las equivalencias fiscales permanecen vacías o deshabilitadas hasta confirmar los datos de JOMLIA. La [guía de configuración](JomliaWebPosSync/CONFIGURATION.md) define cada clave y sus reglas.

## Límites

- Solo facturas nuevas posteriores al corte, emitidas y no anuladas.
- No hay dashboard, API, base adicional, triggers en Firebird ni escritura directa en tablas WebPOS.
- Se conservan las validaciones fiscales indispensables, incluidas las tasas canónicas de ITBIS: 0/3/4 = 0%, 1 = 18%, 2 = 16%.
- El contrato JSON, la serialización determinista, los mapas y la idempotencia del SP no cambian.

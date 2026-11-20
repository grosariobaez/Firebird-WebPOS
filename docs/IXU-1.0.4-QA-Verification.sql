/* IXU 1.0.4 - QA Verification (solo lectura)
   Factura objetivo: IDCOMPANIA=1, IDFACTURA=572, NCF=E310000000004
   Sustituir @CompanyLicCod/@BranchCod/@PosCod con valores reales de QA antes de ejecutar.
   No modifica datos. Ejecutar en SQL Server QA (webpos_rd). */
USE [webpos_rd];
GO

DECLARE @SourceSystem nvarchar(40) = N'JOMLIA';
DECLARE @IdCompania int = 1;
DECLARE @IdFactura int = 572;
DECLARE @DocType nvarchar(2) = N'F';
DECLARE @DocNumber nvarchar(50) = N'E310000000004';
DECLARE @CompanyLicCod nvarchar(10) = N'<CompanyLicCod_QA>';
DECLARE @BranchCod nvarchar(4) = N'<BranchCod_QA>';
DECLARE @PosCod nvarchar(4) = N'<PosCod_QA>';

-- 1) SourceMap (origen -> destino) e idempotencia base
SELECT 'SourceMap' AS Seccion, *
FROM dbo.PA_FB_SourceMap
WHERE sourceSystem = @SourceSystem AND idCompania = @IdCompania AND idFactura = @IdFactura;

-- 2) Header destino (columnas contrato WebPOS V6 / SP v3 QA)
SELECT 'Header' AS Seccion,
       id, docToPrint, companyLicCod, branchCod, posCod, docType, docNumber,
       systemRef, docNumberDueDate, docDate, customerName, customerRUC,
       customerType, customerAddress, email, currency, currencyRate, taxMode, operMode,
       paymentType, tip, subtotalExento, subtotalGravado, subtotalNet, totalItbis, total,
       procDate, qrL1, qrL2, qrCode
FROM dbo.Header
WHERE companyLicCod = @CompanyLicCod AND branchCod = @BranchCod AND posCod = @PosCod
  AND docType = @DocType AND docNumber = @DocNumber;

-- 3) Detail destino (esperado: 1 línea precio 3500, damt 0, tax 1, itemClass 0, qty 1)
SELECT 'Detail' AS Seccion,
       companyLicCod, branchCod, posCod, docType, docNumber, qty, price, code, [desc],
       comments, tax, itemClass, damt, UM,
       CONVERT(decimal(18,2), ROUND(CONVERT(decimal(25,4), qty) * price - damt, 2)) AS lineBase
FROM dbo.Detail
WHERE companyLicCod = @CompanyLicCod AND branchCod = @BranchCod AND posCod = @PosCod
  AND docType = @DocType AND docNumber = @DocNumber
ORDER BY code, [desc];

-- 4) Payment destino (esperado: 1 pago crédito tipo 04, monto 4130.00)
SELECT 'Payment' AS Seccion,
       companyLicCod, branchCod, posCod, docType, docNumber, [type], amt, desc1
FROM dbo.Payment
WHERE companyLicCod = @CompanyLicCod AND branchCod = @BranchCod AND posCod = @PosCod
  AND docType = @DocType AND docNumber = @DocNumber
ORDER BY [type], amt;

-- 5) Conteos e idempotencia (esperado: Header=1, Detail=1, Payment=1, SourceMap=1)
SELECT 'Counts' AS Seccion,
       (SELECT COUNT(*) FROM dbo.Header  WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber) AS HeaderCount,
       (SELECT COUNT(*) FROM dbo.Detail  WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber) AS DetailCount,
       (SELECT COUNT(*) FROM dbo.Payment WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber) AS PaymentCount,
       (SELECT COUNT(*) FROM dbo.PA_FB_SourceMap WHERE sourceSystem=@SourceSystem AND idCompania=@IdCompania AND idFactura=@IdFactura) AS SourceMapCount;

-- 6) Verificación fiscal/calculada (total 4130.00, itbis 630.00, base 3500.00)
SELECT 'Reconciliation' AS Seccion,
       h.total, h.totalItbis,
       (SELECT CONVERT(decimal(18,2), SUM(ROUND(CONVERT(decimal(25,4), d.qty) * d.price - d.damt, 2)))
        FROM dbo.Detail d WHERE d.companyLicCod=h.companyLicCod AND d.branchCod=h.branchCod AND d.posCod=h.posCod AND d.docType=h.docType AND d.docNumber=h.docNumber) AS baseDetail,
       (SELECT CONVERT(decimal(18,2), SUM(p.amt))
        FROM dbo.Payment p WHERE p.companyLicCod=h.companyLicCod AND p.branchCod=h.branchCod AND p.posCod=h.posCod AND p.docType=h.docType AND p.docNumber=h.docNumber) AS totalPagos
FROM dbo.Header h
WHERE h.companyLicCod=@CompanyLicCod AND h.branchCod=@BranchCod AND h.posCod=@PosCod
  AND h.docType=@DocType AND h.docNumber=@DocNumber;

-- 7) Idempotencia estricta (sin duplicados por identidad comercial destino)
SELECT 'DestinationDuplicates' AS Seccion, companyLicCod, branchCod, posCod, docType, docNumber, COUNT(*) AS Registros
FROM dbo.Header
WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber
GROUP BY companyLicCod, branchCod, posCod, docType, docNumber
HAVING COUNT(*) > 1;

-- 8) Segunda pasada (payload idéntico) debe ser no-op: ejecutar SP de importación nuevamente en QA
--    y re-ejecutar secciones 5,6,7; los conteos deben permanecer en 1 y no deben aparecer duplicados.
GO

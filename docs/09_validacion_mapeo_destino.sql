/* SQL SERVER / SOLO LECTURA: ejecutar en una COPIA tras importacion QA. */
USE [webpos_rd];
GO
-- Poner variables para factura importada de prueba.
DECLARE @DocNumber nvarchar(50)=N'E320000000001';
DECLARE @CompanyLicCod nvarchar(10)=N'CODIGO_QA';
DECLARE @BranchCod nvarchar(4)=N'0001';
DECLARE @PosCod nvarchar(4)=N'001';
DECLARE @DocType nvarchar(2)=N'F';

SELECT id,docNumber,docType,docToPrint,docDate,docNumberDueDate,
       taxMode,customerType,totalItbis,total,procDate
FROM dbo.Header
WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod
  AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber;

SELECT COUNT_BIG(*) AS lineas, SUM(CONVERT(decimal(25,4),qty)*price-damt) AS baseLineas,
       SUM(CASE WHEN tax NOT IN (0,1,2,3,4) THEN 1 ELSE 0 END) AS taxInvalidos
FROM dbo.Detail
WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod
  AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber;

SELECT [type], COUNT_BIG(*) AS formas, SUM(amt) AS totalPago
FROM dbo.Payment
WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod
  AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber
GROUP BY [type];

SELECT * FROM dbo.PA_FB_SourceMap
WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod
  AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber;
GO

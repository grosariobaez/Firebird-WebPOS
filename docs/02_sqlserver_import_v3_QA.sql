/*
  JOMLIA -> webpos_rd | V3 QA | Alineado con WPFeDo DBConn V6.
  NO APTO PARA PRODUCCION SIN QA con datos y servidores reales.
  Secuencia de escritura WebPOS V6: Payment -> Detail -> Header (docToPrint=1),
  dentro de una unica transaccion en SQL Server.
  Exclusivo para facturas F eNCF E31,E32,E44,E45,E46; otros tipos quedan bloqueados.
  Requiere SQL Server 2016+ / nivel de compatibilidad >=130 (OPENJSON),
  CREATE OR ALTER requiere SQL Server 2016 SP1+.
*/
USE [webpos_rd];
GO

IF OBJECT_ID(N'dbo.PA_FB_SourceMap',N'U') IS NULL
BEGIN
  CREATE TABLE dbo.PA_FB_SourceMap (
    sourceSystem nvarchar(40) NOT NULL,
    idCompania int NOT NULL,
    idFactura int NOT NULL,
    companyLicCod nvarchar(10) NOT NULL,
    branchCod nvarchar(4) NOT NULL,
    posCod nvarchar(4) NOT NULL,
    docType nvarchar(2) NOT NULL,
    docNumber nvarchar(50) NOT NULL,
    payloadHash varbinary(32) NOT NULL,
    createdAt datetime2(3) NOT NULL
      CONSTRAINT DF_PA_FB_SourceMap_createdAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_PA_FB_SourceMap PRIMARY KEY (sourceSystem,idCompania,idFactura),
    CONSTRAINT UQ_PA_FB_SourceMap_Target UNIQUE (companyLicCod,branchCod,posCod,docType,docNumber)
  );
END;
GO

IF OBJECT_ID(N'dbo.PA_FB_PaymentTypeMap',N'U') IS NULL
BEGIN
  CREATE TABLE dbo.PA_FB_PaymentTypeMap (
    fpaTipo nvarchar(3) NOT NULL PRIMARY KEY,
    webposTipo nvarchar(2) NOT NULL,
    descripcion nvarchar(80) NULL
  );
  -- VACIA: poblarla SOLAMENTE con codigos FPA_TIPO comprobados en JOMLIA.
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_ImportarFacturaFirebird
  @SourceSystem nvarchar(40),
  @CompanyLicCod nvarchar(10),
  @BranchCod nvarchar(4),
  @PosCod nvarchar(4),
  @Payload nvarchar(max),
  @DocNumber nvarchar(50) OUTPUT,
  @AllowMonetaryLineDiscount bit = 0
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  IF NULLIF(LTRIM(RTRIM(@SourceSystem)),N'') IS NULL
    OR NULLIF(LTRIM(RTRIM(@CompanyLicCod)),N'') IS NULL
    OR NULLIF(LTRIM(RTRIM(@BranchCod)),N'') IS NULL
    OR NULLIF(LTRIM(RTRIM(@PosCod)),N'') IS NULL
    THROW 51101,'Configuracion del origen/destino incompleta.',1;
  IF ISNULL(ISJSON(@Payload),0) <> 1
    THROW 51102,'Payload JSON invalido.',1;
  IF LEFT(LTRIM(COALESCE(JSON_QUERY(@Payload,'$.details'),N'')),1) <> N'['
    OR LEFT(LTRIM(COALESCE(JSON_QUERY(@Payload,'$.payments'),N'')),1) <> N'['
    THROW 51103,'El JSON debe tener arreglos details y payments.',1;

  DECLARE
    @IdCompania int = TRY_CONVERT(int,JSON_VALUE(@Payload,'$.idCompania')),
    @IdFactura int = TRY_CONVERT(int,JSON_VALUE(@Payload,'$.idFactura')),
    @DocType nvarchar(2) = NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,'$.header.docType'))),N''),
    @Fecha datetime2(3) = TRY_CONVERT(datetime2(3),JSON_VALUE(@Payload,'$.header.fecha'),126),
    @FechaVencRaw nvarchar(40) = NULLIF(JSON_VALUE(@Payload,'$.header.ncfVencimiento'),N''),
    @FechaVenc date = TRY_CONVERT(date,JSON_VALUE(@Payload,'$.header.ncfVencimiento'),23),
    @Total decimal(18,2) = TRY_CONVERT(decimal(18,2),JSON_VALUE(@Payload,'$.header.total')),
    @Itbis decimal(18,2) = TRY_CONVERT(decimal(18,2),JSON_VALUE(@Payload,'$.header.itbis')),
    @Nombre nvarchar(150) = NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,'$.header.nombre'))),N''),
    @RUC nvarchar(20) = NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,'$.header.ruc'))),N''),
    @Direccion nvarchar(100) = NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,'$.header.direccion'))),N''),
    @Email nvarchar(80) = NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,'$.header.email'))),N''),
    @CustomerType nvarchar(2) = NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,'$.header.customerType'))),N''),
    @TaxMode int = TRY_CONVERT(int,JSON_VALUE(@Payload,'$.header.taxMode')),
    @OperMode nvarchar(2) = COALESCE(NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,'$.header.operMode'))),N''),N'01'),
    @Currency nvarchar(3) = COALESCE(NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,'$.header.currency'))),N''),N'DOP'),
    @CurrencyRate decimal(8,4) = TRY_CONVERT(decimal(8,4),JSON_VALUE(@Payload,'$.header.currencyRate')),
    @PaymentType int = TRY_CONVERT(int,JSON_VALUE(@Payload,'$.header.paymentType')),
    @Tip int = COALESCE(TRY_CONVERT(int,JSON_VALUE(@Payload,'$.header.tip')),0),
    @Devuelto decimal(18,2) = TRY_CONVERT(decimal(18,2),JSON_VALUE(@Payload,'$.header.devuelto')),
    @SubtotalExento decimal(18,2) = TRY_CONVERT(decimal(18,2),JSON_VALUE(@Payload,'$.header.subtotalExento')),
    @SubtotalGravado decimal(18,2) = TRY_CONVERT(decimal(18,2),JSON_VALUE(@Payload,'$.header.subtotalGravado')),
    @SubtotalNet decimal(18,2) = TRY_CONVERT(decimal(18,2),JSON_VALUE(@Payload,'$.header.subtotalNet')),
    @Hash varbinary(32) = HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Payload)),
    @TotalBase decimal(18,2),
    @TotalPagos decimal(18,2),
    @HashPrevio varbinary(32),
    @DocNumberPrevio nvarchar(50),
    @LockResult int,
    @LockSource nvarchar(255),
    @LockTarget nvarchar(255);

  SET @DocNumber = NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,'$.header.ncf'))),N'');
  IF @IdCompania IS NULL OR @IdFactura IS NULL OR @IdCompania < 0 OR @IdFactura < 0
    THROW 51104,'Identidad Firebird invalida.',1;
  IF @Fecha IS NULL OR @Fecha < '17530101'
    OR @Total IS NULL OR @Total < 0 OR @Itbis IS NULL OR @Itbis < 0
    THROW 51105,'Fecha, total o ITBIS invalidos.',1;
  IF @DocNumber IS NULL OR LEN(@DocNumber) <> 13 OR LEFT(@DocNumber,1) <> N'E'
    THROW 51106,'DocNumber debe ser un eNCF de 13 caracteres.',1;
  IF @DocType IS NULL OR @DocType <> N'F' OR LEFT(@DocNumber,3) NOT IN (N'E31',N'E32',N'E44',N'E45',N'E46')
    THROW 51107,'Solo facturas tipo F (E31/E32/E44/E45/E46) habilitadas en esta fase.',1;
  IF @FechaVencRaw IS NOT NULL AND @FechaVenc IS NULL
    THROW 51108,'ncfVencimiento no es fecha ISO yyyy-MM-dd.',1;
  IF LEFT(@DocNumber,3) <> N'E32' AND @FechaVenc IS NULL
    THROW 51109,'WebPOS V6 requiere vencimiento eNCF para este tipo de comprobante.',1;
  IF @CustomerType IS NULL AND LEFT(@DocNumber,3) = N'E32'
    SET @CustomerType = N'07';
  IF @CustomerType NOT IN (N'03',N'04',N'05',N'06',N'07') OR @CustomerType IS NULL
    THROW 51110,'CustomerType debe ser 03,04,05,06 o 07.',1;
  IF (@Nombre IS NULL OR @RUC IS NULL OR @Direccion IS NULL) AND @CustomerType <> N'07'
    THROW 51111,'Cliente no consumidor final requiere nombre, identificacion y direccion.',1;
  IF @Nombre IS NULL AND @CustomerType = N'07'
    SET @Nombre = N'Consumidor Final';
  IF LEFT(@DocNumber,3) <> N'E32' AND @CustomerType = N'07'
    THROW 51112,'Tipo de comprobante requiere identificacion del comprador.',1;
  IF @TaxMode IS NULL OR @TaxMode <> 0
    THROW 51113,'V6 exige configurar TaxMode=0 para este flujo (precios sin ITBIS).',1;
  IF @OperMode NOT IN (N'01',N'02',N'03',N'04',N'05',N'06')
    THROW 51114,'OperMode no admitido por WebPOS V6.',1;
  IF @Currency NOT IN (N'DOP',N'USD',N'EUR')
    THROW 51115,'Moneda no configurada en WebPOS V6.',1;
  IF @Currency = N'DOP' AND @CurrencyRate IS NULL SET @CurrencyRate = 1;
  IF @CurrencyRate IS NULL OR @CurrencyRate <= 0
    OR (@Currency = N'DOP' AND @CurrencyRate <> 1)
    THROW 51116,'Tasa de cambio invalida para moneda.',1;
  IF @PaymentType IS NOT NULL AND @PaymentType NOT IN (1,2,3)
    THROW 51117,'paymentType debe ser NULL o 1,2,3.',1;
  IF @Tip <> 0 OR (@Devuelto IS NOT NULL AND @Devuelto <> 0)
    THROW 51118,'Propina legal o devolucion no habilitada sin conciliacion especifica.',1;
  IF (@SubtotalExento IS NOT NULL AND @SubtotalExento < 0)
    OR (@SubtotalGravado IS NOT NULL AND @SubtotalGravado < 0)
    OR (@SubtotalNet IS NOT NULL AND @SubtotalNet < 0)
    THROW 51119,'Subtotales negativos.',1;
  -- No truncar textos del JSON para adaptarlos silenciosamente al DDL/especificacion.
  IF LEN(COALESCE(JSON_VALUE(@Payload,'$.header.nombre'),N'')) > 150
    OR LEN(COALESCE(JSON_VALUE(@Payload,'$.header.ruc'),N'')) > 20
    OR LEN(COALESCE(JSON_VALUE(@Payload,'$.header.direccion'),N'')) > 100
    OR LEN(COALESCE(JSON_VALUE(@Payload,'$.header.email'),N'')) > 80
    THROW 51120,'Longitud de datos cliente excede los limites de destino.',1;

  IF EXISTS (
    SELECT 1 FROM OPENJSON(@Payload,'$.details') WITH (
      descripcion nvarchar(max) '$.descripcion',
      referencia nvarchar(max) '$.referencia',
      um nvarchar(max) '$.um',
      dperc nvarchar(max) '$.dperc'
    ) D WHERE LEN(COALESCE(D.descripcion,N'')) > 80
       OR LEN(COALESCE(D.referencia,N'')) > 35
       OR LEN(COALESCE(D.um,N'')) > 10
       OR D.dperc IS NOT NULL
  ) THROW 51121,'Longitudes de detalle invalidas o dperc no habilitado.',1;

  DECLARE @Detalle TABLE (
    secuencia int NOT NULL PRIMARY KEY,
    idProducto int NULL,
    referencia nvarchar(35) NULL,
    descripcion nvarchar(80) NULL,
    cantidad decimal(16,2) NULL,
    precio decimal(18,4) NULL,
    descuento decimal(16,2) NULL,
    itbis decimal(16,2) NULL,
    cantidadDevuelta decimal(16,2) NULL,
    tax int NULL,
    itemClass int NULL,
    UM nvarchar(10) NULL
  );
  INSERT INTO @Detalle
    (secuencia,idProducto,referencia,descripcion,cantidad,precio,
     descuento,itbis,cantidadDevuelta,tax,itemClass,UM)
  SELECT secuencia,idProducto,referencia,descripcion,cantidad,precio,
         descuento,itbis,cantidadDevuelta,tax,itemClass,UM
  FROM OPENJSON(@Payload,'$.details') WITH (
    secuencia int '$.secuencia',
    idProducto int '$.idProducto',
    referencia nvarchar(35) '$.referencia',
    descripcion nvarchar(80) '$.descripcion',
    cantidad decimal(16,2) '$.cantidad',
    precio decimal(18,4) '$.precio',
    descuento decimal(16,2) '$.descuento',
    itbis decimal(16,2) '$.itbis',
    cantidadDevuelta decimal(16,2) '$.cantidadDevuelta',
    tax int '$.tax',
    itemClass int '$.itemClass',
    UM nvarchar(10) '$.um'
  );
  IF NOT EXISTS (SELECT 1 FROM @Detalle)
    THROW 51122,'Factura sin lineas.',1;
  IF EXISTS (
    SELECT 1 FROM @Detalle WHERE
      secuencia IS NULL OR idProducto IS NULL OR cantidad IS NULL OR cantidad <= 0 OR
      precio IS NULL OR precio < 0 OR descuento IS NULL OR descuento < 0 OR
      itbis IS NULL OR itbis < 0 OR cantidadDevuelta IS NULL OR cantidadDevuelta <> 0 OR
      tax IS NULL OR tax NOT IN (0,1,2,3,4) OR itemClass IS NULL OR itemClass NOT IN (0,1) OR
      descuento > ROUND(CONVERT(decimal(25,4),cantidad)*precio,2) OR
      (precio=0 AND tax<>4) OR
      (descuento=ROUND(CONVERT(decimal(25,4),cantidad)*precio,2) AND tax<>4) OR
      (tax=3 AND LEFT(@DocNumber,3) <> N'E46')
  ) THROW 51123,'Detalle no cumple reglas WebPOS V6.',1;
  IF @AllowMonetaryLineDiscount <> 1
    AND EXISTS (SELECT 1 FROM @Detalle WHERE descuento <> 0)
    THROW 51124,'Descuento bloqueado: confirmar monto por linea en DFC_DESCUENTO.',1;

  SELECT @TotalBase = CONVERT(decimal(18,2),ISNULL(SUM(
    ROUND(CONVERT(decimal(25,4),cantidad)*precio-descuento,2)
  ),0)) FROM @Detalle;
  -- TaxMode 0: precio NETO de ITBIS. No tratar precios inclusivos como exclusivos.
  IF @TotalBase + @Itbis <> @Total
    THROW 51125,'Descuadre: SUM(qty*price-damt)+FAC_ITBIS <> FAC_TOTAL (TaxMode=0).',1;

  IF EXISTS (
    SELECT 1 FROM OPENJSON(@Payload,'$.payments') WITH (
      descripcion nvarchar(max) '$.descripcion'
    ) P WHERE LEN(COALESCE(P.descripcion,N'')) > 20
  ) THROW 51126,'Payment.desc1 excede 20 caracteres (limite de la especificacion).',1;

  DECLARE @Pagos TABLE (
    secuencia int NOT NULL PRIMARY KEY,
    fpaTipo nvarchar(3) NULL,
    monto decimal(16,2) NULL,
    estatus nvarchar(3) NULL,
    descripcion nvarchar(20) NULL
  );
  INSERT INTO @Pagos (secuencia,fpaTipo,monto,estatus,descripcion)
  SELECT secuencia,NULLIF(LTRIM(RTRIM(tipo)),N''),monto,estatus,descripcion
  FROM OPENJSON(@Payload,'$.payments') WITH (
    secuencia int '$.secuencia',
    tipo nvarchar(3) '$.tipo',
    monto decimal(16,2) '$.monto',
    estatus nvarchar(3) '$.estatus',
    descripcion nvarchar(20) '$.descripcion'
  );
  IF NOT EXISTS (SELECT 1 FROM @Pagos)
    THROW 51127,'Factura sin pagos.',1;
  IF EXISTS (SELECT 1 FROM @Pagos WHERE estatus IS NULL OR estatus <> N'EMI'
             OR fpaTipo IS NULL OR monto IS NULL OR monto < 0)
    THROW 51128,'Pagos incompletos/no EMI o montos/tipos invalidos.',1;
  IF EXISTS (
    SELECT 1 FROM @Pagos P
    LEFT JOIN dbo.PA_FB_PaymentTypeMap M ON M.fpaTipo=P.fpaTipo
    WHERE M.fpaTipo IS NULL OR M.webposTipo NOT IN
      (N'01',N'02',N'03',N'04',N'05',N'06',N'07',N'08')
  ) THROW 51129,'Falta equivalencia valida FPA_TIPO -> Payment.type (01..08).',1;
  IF LEFT(@DocNumber,3) <> N'E32' AND EXISTS (
    SELECT 1 FROM @Pagos P
    JOIN dbo.PA_FB_PaymentTypeMap M ON M.fpaTipo=P.fpaTipo
    WHERE M.webposTipo=N'05'
  ) THROW 51130,'Pago con certificado de regalo tipo 05 requiere eCF E32.',1;
  SELECT @TotalPagos = CONVERT(decimal(18,2),SUM(monto)) FROM @Pagos;
  IF @TotalPagos <> @Total
    THROW 51131,'SUM(FPA_MONTO) <> FAC_TOTAL: revisar cambio, mixto o credito.',1;

  SET @LockSource=CONCAT(N'PAFB:S:',@SourceSystem,N':',@IdCompania,N':',@IdFactura);
  SET @LockTarget=CONCAT(N'PAFB:T:',@CompanyLicCod,N':',@BranchCod,N':',@PosCod,N':',@DocType,N':',@DocNumber);

  BEGIN TRY
    BEGIN TRANSACTION;
    EXEC @LockResult=sys.sp_getapplock @Resource=@LockSource,@LockMode='Exclusive',
      @LockOwner='Transaction',@LockTimeout=10000;
    IF @LockResult<0 THROW 51132,'No se pudo obtener el lock de origen.',1;
    EXEC @LockResult=sys.sp_getapplock @Resource=@LockTarget,@LockMode='Exclusive',
      @LockOwner='Transaction',@LockTimeout=10000;
    IF @LockResult<0 THROW 51133,'No se pudo obtener el lock de destino.',1;

    SELECT @HashPrevio=payloadHash,@DocNumberPrevio=docNumber
    FROM dbo.PA_FB_SourceMap WITH (UPDLOCK,HOLDLOCK)
    WHERE sourceSystem=@SourceSystem AND idCompania=@IdCompania AND idFactura=@IdFactura;

    IF @HashPrevio IS NOT NULL
    BEGIN
      IF @HashPrevio<>@Hash OR @DocNumberPrevio<>@DocNumber
        THROW 51134,'Factura origen cambio despues de sincronizada: revision manual.',1;
      IF NOT EXISTS (
        SELECT 1 FROM dbo.Header WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod
          AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber
      ) OR NOT EXISTS (
        SELECT 1 FROM dbo.Detail WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod
          AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber
      ) OR NOT EXISTS (
        SELECT 1 FROM dbo.Payment WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod
          AND posCod=@PosCod AND docType=@DocType AND docNumber=@DocNumber
      ) THROW 51135,'Mapeo existente sin trio Header/Detail/Payment.',1;
      COMMIT TRANSACTION;
      RETURN;  -- reintento identico: NO DUPLICAR
    END;

    -- Detectar colisiones y residuos; no mezclar registros parciales de otro origen.
    IF EXISTS (
      SELECT 1 FROM dbo.Header WITH (UPDLOCK,HOLDLOCK)
      WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod AND posCod=@PosCod
        AND docType=@DocType AND docNumber=@DocNumber
    ) OR EXISTS (
      SELECT 1 FROM dbo.Detail WITH (UPDLOCK,HOLDLOCK)
      WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod AND posCod=@PosCod
        AND docType=@DocType AND docNumber=@DocNumber
    ) OR EXISTS (
      SELECT 1 FROM dbo.Payment WITH (UPDLOCK,HOLDLOCK)
      WHERE companyLicCod=@CompanyLicCod AND branchCod=@BranchCod AND posCod=@PosCod
        AND docType=@DocType AND docNumber=@DocNumber
    ) THROW 51136,'Documento destino ya ocupado (o datos huerfanos).',1;

    /* EXIGENCIA V6: 1) PAYMENT 2) DETAIL 3) HEADER listo al final. */
    INSERT INTO dbo.Payment
      (companyLicCod,branchCod,posCod,docType,docNumber,[type],amt,desc1)
    SELECT @CompanyLicCod,@BranchCod,@PosCod,@DocType,@DocNumber,
           M.webposTipo,P.monto,P.descripcion
    FROM @Pagos P
    JOIN dbo.PA_FB_PaymentTypeMap M ON M.fpaTipo=P.fpaTipo;

    INSERT INTO dbo.Detail
      (companyLicCod,branchCod,posCod,docType,docNumber,qty,price,code,[desc],
       comments,tax,itemClass,damt,UM)
    SELECT @CompanyLicCod,@BranchCod,@PosCod,@DocType,@DocNumber,cantidad,precio,
           COALESCE(NULLIF(LTRIM(RTRIM(referencia)),N''),CONVERT(nvarchar(35),idProducto)),
           COALESCE(NULLIF(LTRIM(RTRIM(descripcion)),N''),N'Sin descripcion'),
           CASE WHEN descuento>0 THEN N'Descuento segun JOMLIA' ELSE NULL END,
           tax,itemClass,descuento,UM
    FROM @Detalle;

    -- Header al final: docToPrint=1 habilita procesamiento de WebPOS.
    INSERT INTO dbo.Header
      (docToPrint,companyLicCod,branchCod,posCod,docType,docNumber,
       systemRef,docNumberDueDate,docDate,customerName,customerRUC,
       customerType,customerAddress,email,currency,currencyRate,taxMode,operMode,
       paymentType,tip,subtotalExento,subtotalGravado,subtotalNet,totalItbis,total)
    VALUES
      (1,@CompanyLicCod,@BranchCod,@PosCod,@DocType,@DocNumber,
       CONVERT(nvarchar(20),@IdFactura),@FechaVenc,@Fecha,@Nombre,COALESCE(@RUC,N''),
       @CustomerType,@Direccion,@Email,@Currency,@CurrencyRate,0,@OperMode,
       @PaymentType,@Tip,@SubtotalExento,@SubtotalGravado,@SubtotalNet,@Itbis,@Total);
    -- procDate, qrL1, qrL2, qrCode: reservados a procesos de WebPOS; no sobrescribir.

    INSERT INTO dbo.PA_FB_SourceMap
      (sourceSystem,idCompania,idFactura,companyLicCod,branchCod,
       posCod,docType,docNumber,payloadHash)
    VALUES
      (@SourceSystem,@IdCompania,@IdFactura,@CompanyLicCod,@BranchCod,
       @PosCod,@DocType,@DocNumber,@Hash);

    COMMIT TRANSACTION;
  END TRY
  BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
  END CATCH;
END;
GO

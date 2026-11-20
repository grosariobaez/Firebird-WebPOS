#ifndef AppVersion
  #define AppVersion "1.0.4"
#endif

[Setup]
AppId={{A2F45B0E-6C2D-4C58-9F6B-2D3E5A1C8F71}
AppName=IXU
AppVersion={#AppVersion}
AppVerName=IXU {#AppVersion} - INNOVAXD UX SRL
AppPublisher=INNOVAXD UX SRL
AppPublisherURL=https://innovaxd.ca/es/
AppCopyright=INNOVAXD UX SRL
AppComments=IXU. Inteligencia · Ejecución · Utilidad. Tecnología para operaciones del mundo real.
DefaultDirName={autopf}\JomliaWebPosSync
DefaultGroupName=IXU
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=JomliaWebPosSync-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
UninstallDisplayIcon={app}\JomliaWebPosSync.exe
CloseApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked

[Dirs]
Name: "{commonappdata}\PuntoAparte\JomliaWebPosSync"; Permissions: system-full admins-full; Flags: uninsneveruninstall
Name: "{commonappdata}\PuntoAparte\JomliaWebPosSync\Outbox"; Permissions: system-full admins-full; Flags: uninsneveruninstall
Name: "{commonappdata}\PuntoAparte\JomliaWebPosSync\Logs"; Permissions: system-full admins-full; Flags: uninsneveruninstall

[Files]
Source: ".publish\appsettings.json"; DestDir: "{app}"; DestName: "appsettings.json"; Flags: onlyifdoesntexist uninsneveruninstall
Source: ".publish\appsettings.json"; DestDir: "{app}"; DestName: "appsettings.default.json"; Flags: ignoreversion
Source: ".publish\*"; Excludes: "appsettings.json,appsettings.example.json,appsettings.Local.json"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: ".publish-configurator\*"; DestDir: "{app}\Configurator"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\PRODUCTION_INSTALLATION.md"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\JomliaWebPosSync\CONFIGURATION.md"; DestDir: "{app}\Docs"; DestName: "CONFIGURATION.md"; Flags: ignoreversion

[Icons]
Name: "{group}\IXU Configurador"; Filename: "{app}\Configurator\JomliaWebPosSync.Configurator.exe"; WorkingDir: "{app}"; Comment: "IXU - INNOVAXD UX SRL"
Name: "{group}\Documentación de instalación"; Filename: "{app}\Docs\PRODUCTION_INSTALLATION.md"
Name: "{group}\Desinstalar IXU JomliaWebPosSync"; Filename: "{uninstallexe}"
Name: "{autodesktop}\IXU Configurador"; Filename: "{app}\Configurator\JomliaWebPosSync.Configurator.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "IXU - Tecnología para operaciones del mundo real"

[Run]
Filename: "{app}\Configurator\JomliaWebPosSync.Configurator.exe"; Description: "Abrir configurador IXU"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent runascurrentuser

[Code]
const
  ServiceName = 'JomliaWebPosSync';
  LegacyServiceName = 'IXU';
  ServiceDisplayName = 'IXU JomliaWebPosSync';
  ServiceDescription = 'IXU (INNOVAXD UX SRL). Tecnología para operaciones del mundo real. Servicio de sincronizacion JOMLIA hacia WebPOS.';

procedure RunSc(const Params: string; const Required: Boolean);
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if Required then
    begin
      MsgBox('No se pudo ejecutar sc.exe para configurar el servicio.', mbError, MB_OK);
      Abort;
    end;
    Exit;
  end;
  if Required and (ResultCode <> 0) then
  begin
    MsgBox('sc.exe fallo con codigo ' + IntToStr(ResultCode) + ':' + #13#10 + Params, mbError, MB_OK);
    Abort;
  end;
end;

function ServiceExists(const Name: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if Exec(ExpandConstant('{sys}\sc.exe'), 'query ' + Name, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode = 0;
end;

procedure RemoveLegacyService();
begin
  if ServiceExists(LegacyServiceName) then
  begin
    RunSc('stop ' + LegacyServiceName, False);
    RunSc('delete ' + LegacyServiceName, False);
  end;
end;

procedure EnsureService();
var
  BinPath: string;
begin
  RemoveLegacyService();
  BinPath := '"' + ExpandConstant('{app}\JomliaWebPosSync.exe') + '"';
  if ServiceExists(ServiceName) then
  begin
    RunSc('stop ' + ServiceName, False);
    RunSc('config ' + ServiceName + ' binPath= ' + BinPath + ' start= auto DisplayName= "' + ServiceDisplayName + '"', True);
  end
  else
    RunSc('create ' + ServiceName + ' binPath= ' + BinPath + ' start= auto DisplayName= "' + ServiceDisplayName + '"', True);
  RunSc('description ' + ServiceName + ' "' + ServiceDescription + '"', False);
  RunSc('failure ' + ServiceName + ' reset= 60 actions= restart/60000/restart/60000/restart/60000', False);
  RunSc('start ' + ServiceName, False);
end;

procedure EnsureEventSource();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\eventcreate.exe'),
    '/L APPLICATION /T INFORMATION /SO JomliaWebPosSync /ID 1 /D "IXU JomliaWebPosSync service"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure ProtectDataDirectory();
var
  ResultCode: Integer;
  DataDirectory: string;
begin
  DataDirectory := ExpandConstant('{commonappdata}\PuntoAparte\JomliaWebPosSync');
  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + DataDirectory + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /T /C',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ProtectDataDirectory();
    EnsureEventSource();
    EnsureService();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RunSc('stop ' + ServiceName, False);
    RunSc('delete ' + ServiceName, False);
  end;
end;
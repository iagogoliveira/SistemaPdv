; Instalador do Sistema PDV (Inno Setup 6). Gere com installer\build.ps1.

#define AppName "Sistema PDV"
#define AppExe "SistemaPDV.exe"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{6C1E2B7A-4F3D-4E8B-9A51-2D7F0C3B9E14}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Sistema PDV
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\SistemaPDV
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=SistemaPDV-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 ou superior (mínimo suportado pelo .NET 10).
MinVersion=10.0.17763
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
; Configuração da máquina e logs: graváveis pelos usuários do caixa. Mantidos na desinstalação.
Name: "{commonappdata}\SistemaPDV"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{commonappdata}\SistemaPDV\logs"; Permissions: users-modify; Flags: uninsneveruninstall

[Files]
; Publicação self-contained: não exige o runtime .NET instalado na máquina.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "appsettings.Development.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  ServerPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  ServerPage := CreateInputQueryPage(wpSelectDir,
    'Banco de dados da loja',
    'Em qual SQL Server este caixa vai gravar as vendas?',
    'No computador principal (servidor) use .\SQLEXPRESS. Nos demais caixas, informe o nome do ' +
    'computador principal, por exemplo SERVIDOR\SQLEXPRESS. O SQL Server Express precisa estar ' +
    'instalado no computador principal antes de abrir o sistema.');
  ServerPage.Add('Servidor SQL:', False);
  ServerPage.Values[0] := GetPreviousData('SqlServer', '.\SQLEXPRESS');
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'SqlServer', ServerPage.Values[0]);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ServerPage.ID) and (Trim(ServerPage.Values[0]) = '') then
  begin
    MsgBox('Informe o servidor SQL.', mbError, MB_OK);
    Result := False;
  end;
end;

function JsonEscape(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Connection: String;
begin
  if CurStep = ssPostInstall then
  begin
    Connection := 'Server=' + Trim(ServerPage.Values[0]) +
      ';Database=SistemaPdv;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=10';
    SaveStringToFile(ExpandConstant('{commonappdata}\SistemaPDV\connection.json'),
      '{' + #13#10 + '  "ConnectionStrings": {' + #13#10 +
      '    "Pdv": "' + JsonEscape(Connection) + '"' + #13#10 +
      '  }' + #13#10 + '}' + #13#10, False);
  end;
end;

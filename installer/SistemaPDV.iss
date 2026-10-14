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
SetupIconFile=..\src\Pdv.Desktop\Assets\app.ico
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
    'Em qual PostgreSQL este caixa vai gravar as vendas?',
    'No computador principal (servidor) use localhost. Nos demais caixas, informe o nome ou IP do ' +
    'computador principal (ex.: SERVIDOR ou 192.168.0.10). O PostgreSQL precisa estar instalado no ' +
    'computador principal, com o usuário do sistema criado, antes de abrir o PDV.');
  ServerPage.Add('Servidor (nome ou IP[:porta]):', False);
  ServerPage.Add('Usuário do banco:', False);
  ServerPage.Add('Senha do banco:', True);
  ServerPage.Values[0] := GetPreviousData('PgServer', 'localhost');
  ServerPage.Values[1] := GetPreviousData('PgUser', 'pdv');
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  // A senha nunca é guardada no registro do Windows.
  SetPreviousData(PreviousDataKey, 'PgServer', ServerPage.Values[0]);
  SetPreviousData(PreviousDataKey, 'PgUser', ServerPage.Values[1]);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ServerPage.ID) and ((Trim(ServerPage.Values[0]) = '') or (Trim(ServerPage.Values[1]) = '')) then
  begin
    MsgBox('Informe o servidor e o usuário do banco.', mbError, MB_OK);
    Result := False;
  end;
end;

function JsonEscape(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

{ Valor entre aspas simples na string de conexão: aceita ; e = na senha. }
function ConnValue(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Server, Host, Port, Connection: String;
  P: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    Server := Trim(ServerPage.Values[0]);
    Host := Server;
    Port := '5432';
    P := Pos(':', Server);
    if P > 0 then
    begin
      Host := Copy(Server, 1, P - 1);
      Port := Copy(Server, P + 1, Length(Server));
    end;
    Connection := 'Host=' + Host + ';Port=' + Port + ';Database=sistema_pdv;Username=' + ConnValue(Trim(ServerPage.Values[1])) +
      ';Password=' + ConnValue(ServerPage.Values[2]) + ';Timeout=10';
    SaveStringToFile(ExpandConstant('{commonappdata}\SistemaPDV\connection.json'),
      '{' + #13#10 + '  "ConnectionStrings": {' + #13#10 +
      '    "Pdv": "' + JsonEscape(Connection) + '"' + #13#10 +
      '  }' + #13#10 + '}' + #13#10, False);
  end;
end;

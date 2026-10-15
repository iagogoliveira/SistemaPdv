; Instalador do Sistema PDV (Inno Setup 6). Gere com installer\build.ps1.
; Dois tipos: "Computador principal" (servidor da loja + caixa) e "Somente caixa".

#define AppName "Sistema PDV"
#define AppExe "SistemaPDV.exe"
#define ServerExe "SistemaPDV.Servidor.exe"
#define ServiceName "SistemaPDV.Servidor"
#define ServerPort "5080"
#ifndef AppVersion
  #define AppVersion "0.5.0"
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

[Types]
Name: "principal"; Description: "Computador principal da loja (servidor + caixa)"
Name: "caixa"; Description: "Somente caixa (conecta ao computador principal)"

[Components]
Name: "servidor"; Description: "Servidor da loja (serviço do Windows; precisa do PostgreSQL neste computador)"; Types: principal
Name: "caixa"; Description: "Programa do caixa"; Types: principal caixa; Flags: fixed

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Components: caixa

[Dirs]
; Configuração e logs do caixa: graváveis pelos usuários do caixa. Mantidos na desinstalação.
Name: "{commonappdata}\SistemaPDV"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{commonappdata}\SistemaPDV\logs"; Permissions: users-modify; Flags: uninsneveruninstall
; Dados do servidor (senha do banco, certificado A1, backups): só administradores e o serviço (ACL ajustada no [Run]).
Name: "{commonappdata}\SistemaPDV\Servidor"; Flags: uninsneveruninstall; Components: servidor

[Files]
; Publicações self-contained: não exigem o runtime .NET instalado na máquina.
Source: "{#PublishDir}\caixa\*"; DestDir: "{app}"; Excludes: "appsettings.Development.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: caixa
Source: "{#PublishDir}\servidor\*"; DestDir: "{app}\Servidor"; Excludes: "appsettings.Development.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: servidor

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Components: caixa
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Pasta do servidor sem herança: SYSTEM e Administradores (os segredos são cifrados com DPAPI da máquina).
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\SistemaPDV\Servidor"" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F"; Flags: runhidden; Components: servidor
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\Servidor\{#ServerExe}\"""" start= delayed-auto DisplayName= ""Sistema PDV - Servidor da loja"""; Flags: runhidden; Components: servidor; Check: not ServiceExists
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Banco de dados, NFC-e e backups do Sistema PDV para os caixas da loja."""; Flags: runhidden; Components: servidor
; Reinicia sozinho se cair (5 s, 5 s, 30 s).
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000"; Flags: runhidden; Components: servidor
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Sistema PDV - Servidor"""; Flags: runhidden; Components: servidor
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Sistema PDV - Servidor"" dir=in action=allow protocol=TCP localport={#ServerPort} profile=private,domain"; Flags: runhidden; Components: servidor
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden; Components: servidor
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent; Components: caixa

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Sistema PDV - Servidor"""; Flags: runhidden; RunOnceId: "DeleteFirewall"

[Code]
var
  DatabasePage: TInputQueryWizardPage;
  ServerPage: TInputQueryWizardPage;

function ServiceExists: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure InitializeWizard;
begin
  DatabasePage := CreateInputQueryPage(wpSelectComponents,
    'Banco de dados da loja',
    'Usuário do PostgreSQL usado pelo servidor da loja',
    'O PostgreSQL precisa estar instalado neste computador, com o banco sistema_pdv e o usuário do sistema criados. ' +
    'A senha é gravada cifrada (DPAPI) na pasta do servidor. Deixe a senha vazia para manter a atual numa atualização.');
  DatabasePage.Add('Usuário do banco:', False);
  DatabasePage.Add('Senha do banco:', True);
  DatabasePage.Values[0] := GetPreviousData('PgUser', 'pdv');

  ServerPage := CreateInputQueryPage(DatabasePage.ID,
    'Servidor da loja',
    'Onde está o servidor do Sistema PDV?',
    'No computador principal use localhost. Nos demais caixas, informe o nome ou IP do computador principal ' +
    '(ex.: SERVIDOR ou 192.168.0.10). Na primeira abertura o caixa pede o código de conexão, ' +
    'mostrado em Retaguarda › Sistema no computador principal.');
  ServerPage.Add('Servidor (nome ou IP[:porta]):', False);
  ServerPage.Values[0] := GetPreviousData('Server', 'localhost');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = DatabasePage.ID) and not WizardIsComponentSelected('servidor');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = ServerPage.ID) and WizardIsComponentSelected('servidor') and (Trim(ServerPage.Values[0]) = '') then
    ServerPage.Values[0] := 'localhost';
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  // A senha nunca é guardada no registro do Windows.
  SetPreviousData(PreviousDataKey, 'PgUser', DatabasePage.Values[0]);
  SetPreviousData(PreviousDataKey, 'Server', ServerPage.Values[0]);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = DatabasePage.ID) and (Trim(DatabasePage.Values[0]) = '') then
  begin
    MsgBox('Informe o usuário do banco.', mbError, MB_OK);
    Result := False;
  end;
  if (CurPageID = ServerPage.ID) and (Trim(ServerPage.Values[0]) = '') then
  begin
    MsgBox('Informe o servidor da loja.', mbError, MB_OK);
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
  Code: Integer;
  ServerJson, Connection: String;
  Existing: AnsiString;
begin
  // Atualização: para o serviço antes de substituir os arquivos.
  if (CurStep = ssInstall) and WizardIsComponentSelected('servidor') and ServiceExists then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(3000);
  end;

  if CurStep = ssPostInstall then
  begin
    if WizardIsComponentSelected('servidor') then
    begin
      ServerJson := ExpandConstant('{commonappdata}\SistemaPDV\Servidor\server.json');
      // Senha em texto só até o servidor iniciar: ele a cifra com DPAPI no próprio arquivo.
      if (DatabasePage.Values[1] <> '') or not FileExists(ServerJson) then
      begin
        Connection := 'Host=localhost;Port=5432;Database=sistema_pdv;Username=' + ConnValue(Trim(DatabasePage.Values[0])) +
          ';Password=' + ConnValue(DatabasePage.Values[1]) + ';Timeout=10';
        SaveStringToFile(ServerJson,
          '{' + #13#10 + '  "ConnectionStrings": {' + #13#10 +
          '    "Pdv": "' + JsonEscape(Connection) + '"' + #13#10 +
          '  }' + #13#10 + '}' + #13#10, False);
      end;
    end;

    // Caixa: só o endereço; a chave de acesso é gerada ao conectar com o código.
    // O connection.json da versão 0.1 (acesso direto ao banco, com a senha) é substituído.
    Existing := '';
    LoadStringFromFile(ExpandConstant('{commonappdata}\SistemaPDV\connection.json'), Existing);
    if Pos('"Address"', Existing) = 0 then
      SaveStringToFile(ExpandConstant('{commonappdata}\SistemaPDV\connection.json'),
        '{' + #13#10 + '  "Pdv": { "Server": { "Address": "' + JsonEscape(Trim(ServerPage.Values[0])) + '" } }' + #13#10 + '}' + #13#10, False);
  end;
end;

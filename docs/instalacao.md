# Instalação e configuração

## Topologia

- **Computador principal:** PostgreSQL 17 + servidor da loja (serviço) + normalmente também um caixa.
- **Demais caixas:** só o programa do caixa, apontando para o computador principal.
- Rede local com o computador principal em IP fixo (ou nome resolvível). O PostgreSQL **não** precisa ficar aberto na
  rede: só o servidor da loja o acessa (localhost).

## Pré-requisitos do computador principal

1. Windows 10 1809+ ou Windows 11, 64 bits.
2. **PostgreSQL 17** com o usuário do sistema:

```bash
psql -h localhost -U postgres -c "CREATE ROLE pdv LOGIN CREATEDB PASSWORD 'troque-esta-senha';"
```

   O servidor cria e migra o banco `sistema_pdv` na primeira execução. As ferramentas `pg_dump`/`pg_restore` (instaladas
   com o PostgreSQL) são usadas pelo backup.

## Instalador

Gerado por `installer/build.ps1` (ver [Desenvolvimento](desenvolvimento.md#instalador)):
`artifacts/installer/SistemaPDV-Setup-<versão>.exe`, self-contained (não exige .NET instalado).

| Tipo | Instala | Pergunta |
|---|---|---|
| Computador principal | Servidor + caixa | Usuário e senha do PostgreSQL; endereço do servidor (`localhost`) |
| Somente caixa | Caixa | Nome ou IP do computador principal |

No computador principal o instalador também:

- cria o serviço `SistemaPDV.Servidor` (início automático atrasado; reinicia sozinho após falha em 5 s, 5 s e 30 s);
- libera a porta TCP **5080** no firewall do Windows (perfis privado e domínio);
- restringe `%ProgramData%\SistemaPDV\Servidor` a Administradores e SYSTEM;
- grava `server.json` com a conexão — a senha é **cifrada pelo servidor ao iniciar** (DPAPI).

Na primeira abertura de cada caixa, informe o código de conexão (Retaguarda › Sistema no principal, ou
`"C:\Program Files\SistemaPDV\Servidor\SistemaPDV.Servidor.exe" --codigo` executado como administrador).

## Pagamentos integrados

- **Pix:** o banco/PSP da loja fornece client ID, client secret, certificado (mTLS) e os endereços da API Pix e do
  token; a chave Pix recebedora é da loja. Configure em Retaguarda › Pagamentos (computador principal com acesso à
  internet). O servidor consulta o PSP; não é preciso abrir portas na internet.
- **TEF:** instale o cliente TEF e o pinpad da credenciadora em cada caixa e informe o provedor em
  `%ProgramData%\SistemaPDV\appsettings.json` (`{ "Pdv": { "Tef": { "Provider": "..." } } }`). Nesta versão há o
  provedor `Simulado` (treinamento); o adaptador da credenciadora dos pilotos é a próxima entrega.

## Atualização

Atualize **o computador principal e todos os caixas para a mesma versão** (caixa com versão diferente não opera). O
instalador para o serviço antes de substituir os arquivos, mantém configurações, dados e backups, e o servidor aplica
as migrações do banco ao iniciar. Na atualização a partir da 0.1 (caixas acessando o banco direto), o `connection.json`
antigo — que tinha a senha do banco — é substituído pelo novo formato.

## Arquivos do caixa

`%ProgramData%\SistemaPDV` (gravável pelos usuários do caixa):

| Arquivo | Conteúdo |
|---|---|
| `connection.json` | Endereço do servidor e chave do caixa (cifrada) |
| `appsettings.json` | Ajustes opcionais: impressora (`Pdv:Receipt:Printer`) e TEF (`Pdv:Tef:Provider`) |
| `tef-pendentes.json` | Cartões aprovados aguardando confirmação (temporário) |
| `venda-em-andamento.json` | Diário da venda em conclusão (temporário) |
| `logs\pdv-AAAAMMDD.log` | Logs técnicos (30 dias) |

## Arquivos do servidor

`%ProgramData%\SistemaPDV\Servidor` (Administradores e SYSTEM):

| Arquivo | Conteúdo |
|---|---|
| `server.json` | Conexão com o banco (senha cifrada) e código de conexão (cifrado) |
| `nfce.json`, `certificado-a1.pfx` | Configuração da NFC-e (senha e CSC cifrados) e certificado A1 |
| `pix.json`, `certificado-psp.pfx` | Pix integrado (client secret e senha do certificado cifrados) e certificado do PSP |
| `backup.json` | Configuração e situação do backup |
| `backups\pdv-AAAAMMDD-HHMMSS.backup` | Backups (formato custom do `pg_dump`) |
| `logs\servidor-AAAAMMDD.log` | Logs do servidor (60 dias) |

Os segredos cifrados com DPAPI só podem ser lidos neste computador: ao trocar o computador principal, restaure um
backup do banco e informe de novo a senha do banco, o certificado/CSC e reconecte os caixas.

## Suporte rápido

| Sintoma | Verificar |
|---|---|
| Caixa: "Sem conexão com o servidor da loja" | Computador principal ligado; serviço `SistemaPDV.Servidor` em execução (`services.msc`); porta 5080 liberada; endereço em `connection.json` |
| Caixa pede o código de novo | Caixa desconectado na retaguarda, chave apagada ou banco restaurado de outra loja |
| "Este caixa está na versão X e o servidor na Y" | Instalar a mesma versão |
| Serviço não inicia | `logs\servidor-*.log`: senha do banco, PostgreSQL parado ou banco de versão mais nova |
| Backup com erro em vermelho | Segunda pasta inacessível (pendrive removido, rede fora); o backup local foi feito |

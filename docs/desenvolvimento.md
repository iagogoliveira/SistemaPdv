# Desenvolvimento

## Ambiente

- .NET SDK 10 (`global.json` fixa a família 10.0 e o Microsoft.Testing.Platform).
- PostgreSQL 17 local com o usuário de desenvolvimento:

```bash
psql -h localhost -U postgres -c "CREATE ROLE pdv LOGIN CREATEDB PASSWORD 'pdv_dev';"
```

  Alternativa: `docker compose up -d` (contêiner `pdv-postgres` com o mesmo usuário, `compose.yaml`).

## Executar

Em dois terminais:

```bash
dotnet run --project src/Pdv.Server
```

```bash
dotnet run --project src/Pdv.Desktop
```

Pagamentos em treinamento: Pix com o provedor "Simulado" (Retaguarda › Pagamentos; o pagamento é "recebido" em ~8 s) e
TEF com `Pdv__Tef__Provider=Simulado` no caixa (valores terminados em ,13 são negados).

O perfil `Development` (`Properties/launchSettings.json`) do servidor usa o banco local `sistema_pdv`, produtos de
demonstração (`7891000000011`, `…028`, `…035`, `…042`, `…059`) e a SEFAZ simulada. O caixa pergunta o servidor
(`localhost`) e conecta sem código.

Vários caixas no mesmo computador: `PDV_DATA_DIR` (pasta de dados) e `Pdv__Terminal__Name` (nome) em cada instância.
Servidor isolado: `Pdv__Server__DataFolder`, `Pdv__Server__Port`, `Pdv__Server__LocalOnly=true`,
`ConnectionStrings__Pdv`.

## Testes

```bash
dotnet test --solution Pdv.slnx
```

Os testes de banco criam um banco próprio por execução (`pdv_tests_*`, `pdv_srv_*`) e o removem ao final.
`PDV_TEST_PG` troca a conexão (ex.: `Host=servidor;Username=pdv;Password=...`). Backup/restauração usam o `pg_dump` local;
sem ele, o do contêiner `pdv-postgres` (`PDV_TEST_PG_CONTAINER`).

| Projeto | Cobre |
|---|---|
| `Pdv.Domain.Tests` | Regras puras |
| `Pdv.Infrastructure.Tests` | Serviços contra PostgreSQL (inclui o "dia completo de loja" da Etapa 4) |
| `Pdv.Fiscal.Tests` | Chave de acesso, XML, assinatura |
| `Pdv.Server.Tests` | Servidor em memória (TestServer) + cliente real: pareamento, versões, isolamento entre caixas, idempotência pela rede, 3 caixas simultâneos, contrato remoto, backup/restauração, DPAPI; TEF e pendências |

Testes de interface (UI Automation) são executados manualmente a cada etapa; capturam só as janelas do PDV e usam banco
descartável.

## Migrações

```bash
dotnet tool restore
```

```bash
dotnet ef migrations add NomeDaMigracao --project src/Pdv.Infrastructure --output-dir Persistence/Migrations
```

O servidor aplica as migrações ao iniciar. Nunca altere uma migração já publicada.

## Instalador

```bash
powershell -ExecutionPolicy Bypass -File installer/build.ps1 -Version 0.6.0
```

Publica caixa e servidor (self-contained, win-x64) em `artifacts/publish/{caixa,servidor}` e compila
`installer/SistemaPDV.iss` com o Inno Setup 6. `-SkipInstaller` só publica. A versão do produto fica em
`Directory.Build.props` e precisa ser a mesma no caixa e no servidor.

## Convenções

- Textos para o usuário em português; código e identificadores em inglês.
- Regras de negócio no domínio; serviços retornam `Result<T>` para falhas esperadas e lançam `DomainException` para
  violações de regra.
- Serviços expostos aos caixas: ver [Servidor da loja › Serviços expostos](servidor-e-caixas.md#serviços-expostos).
- Chamadas do caixa: `runner.RunAsync<Serviço, Retorno>(s => s.Metodo(args, default))` — uma única chamada de método,
  sem argumentos nomeados fora de posição.
- Analisadores `latest-recommended`; a solução compila sem avisos.
- Toda mudança de comportamento atualiza a página correspondente desta documentação e o [CHANGELOG](CHANGELOG.md) no
  mesmo commit.
- Commits em português, descrevendo o que mudou e por quê.

# Licenciamento

## Como funciona

- A licença é um arquivo `.pdvlic` com os dados do cliente assinados pelo **fornecedor** (ECDSA P-256 / SHA-256). O
  sistema traz embutida só a **chave pública** (`src/Pdv.Application/Licensing/LicenseKeys.cs`): consegue conferir,
  mas não emitir licenças.
- Ela vale para **um computador principal** (servidor da loja), identificado pelo **código da máquina** — derivado do
  identificador da instalação do Windows. Trocar de computador ou reinstalar o Windows exige nova licença
  (transferência).
- Funciona **sem internet**: não há ativação online nem consulta periódica ao fornecedor.

| Campo | Significado |
|---|---|
| Cliente / CNPJ | Se o CNPJ for informado, precisa ser o da empresa cadastrada |
| Plano | Nome comercial (ex.: "Loja 3 caixas") |
| Caixas | Número máximo de caixas conectados ao servidor |
| Validade | Data final (assinatura) ou sem validade (perpétua) |
| Máquina | Código do computador principal |

## Situações

| Situação | Quando | Efeito |
|---|---|---|
| Demonstração | Sem licença, até 30 dias após a primeira inicialização do banco | Até 2 caixas; aviso nos últimos 7 dias |
| Ativa | Licença válida desta máquina | Aviso nos últimos 15 dias antes do vencimento |
| Tolerância | Até 15 dias após o vencimento | Tudo funciona, com aviso ao abrir o caixa |
| Restrita | Demonstração encerrada, licença vencida após a tolerância, de outra máquina ou adulterada | **Vendas e abertura de caixa bloqueadas**; retaguarda, relatórios, consultas e backup continuam |

O limite de caixas é conferido ao conectar um caixa novo (reconectar um caixa já conectado não conta de novo). Para
liberar uma vaga, desconecte um caixa em Retaguarda › Sistema.

## Na loja

Retaguarda › Sistema mostra a situação, o plano, os caixas usados e o **código desta máquina** ("Copiar código da
máquina"). Para instalar: "Instalar licença" e escolha o arquivo `.pdvlic` recebido do fornecedor. A licença fica em
`%ProgramData%\SistemaPDV\Servidor\licenca.pdvlic`.

## Para o fornecedor: ferramenta `pdv-licenca`

A ferramenta (`tools/Pdv.LicenseTool`) **não** vai para as lojas.

```bash
dotnet run --project tools/Pdv.LicenseTool -- gerar-chaves
```

Cria `chave-privada.pem` e `chave-publica.pem` em `%USERPROFILE%\SistemaPDV-licencas`. **Faça backup da chave privada
em local seguro e nunca a coloque no repositório** (`*.pem` e `*.pdvlic` estão no `.gitignore`): quem tem a chave privada
emite licenças; perdê-la obriga a trocar a chave pública numa nova versão (as licenças antigas deixam de valer).

```bash
dotnet run --project tools/Pdv.LicenseTool -- emitir --cliente "Papelaria Exemplo" --cnpj 11222333000181 --maquina ABCD-1234-EF56-7890 --caixas 3 --validade 2027-12-31
```

```bash
dotnet run --project tools/Pdv.LicenseTool -- conferir LIC-20261015-ABC123.pdvlic
```

Transferência de máquina: o cliente envia o código do novo computador; emita uma nova licença com o novo código (a
anterior continua presa ao computador antigo).

# Roadmap até a operação em loja real

Decisões tomadas (14/10/2026):

- **Banco:** PostgreSQL (sem limite de tamanho, código aberto). Substitui o SQL Server Express antes de existirem dados reais.
- **Fiscal:** NFC-e com biblioteca própria em C# (DFe.NET / Zeus), sem custo por nota.
- **Regimes:** Simples Nacional (CSOSN) e regime normal (CST) desde o cadastro.
- **UF:** ainda não definida — tudo configurável por loja; a homologação começa quando a UF for escolhida.

Cada etapa termina com testes automatizados passando e um critério de aceite verificável.

## Etapa 1 — Migração para PostgreSQL  ✅ concluída

- Provedor EF Core Npgsql, migrações recriadas.
- Concorrência pela coluna de sistema `xmin`, numeração de vendas por sequência, índice parcial de caixa aberto.
- Busca de produtos sem diferenciar maiúsculas/minúsculas.
- Backup/restauração com `pg_dump`/`pg_restore`.
- Ambiente de desenvolvimento com Docker (`compose.yaml`); instalador e tela de conexão pedem servidor, usuário e senha.

**Aceite:** todos os testes (incluindo concorrência, idempotência e backup/restauração) passando em PostgreSQL. ✅ 32/32 em PostgreSQL 17.11.

## Etapa 2 — Dados da empresa e cadastro fiscal  ✅ concluída

- Tela de empresa/loja: razão social, nome fantasia, CNPJ, IE, endereço, UF, município (código IBGE), regime tributário.
- Produto: NCM, CEST, origem, CFOP, CSOSN ou CST de ICMS, alíquotas, PIS/COFINS; perfis tributários reutilizáveis
  (ex.: "Revenda Simples Nacional") para não preencher item a item.
- Validações (CNPJ, NCM com 8 dígitos, CFOP compatível com operação).
- Cliente opcional na venda: CPF/CNPJ na nota.

**Aceite:** uma venda guarda todos os dados fiscais necessários para montar o XML da NFC-e, mesmo antes da emissão.
✅ Cada item vendido guarda NCM, CEST, origem, CFOP, CSOSN/CST e alíquotas; a venda guarda o CPF/CNPJ do consumidor.
CNPJ alfanumérico (IN RFB 2.229/2024) já aceito. Perfis-modelo vêm com alíquotas zeradas para revisão do contador.

## Etapa 3 — Emissão de NFC-e (homologação)  🟡 implementada; falta homologar na SEFAZ real

- Certificado A1 (arquivo .pfx protegido) e CSC por loja.
- Montagem, assinatura e envio do XML; consulta de status do serviço da SEFAZ.
- Fila de documentos fiscais separada da venda: autorização, rejeição (com motivo), reenvio seguro.
- Contingência offline (emissão em contingência e transmissão posterior).
- Cancelamento e inutilização de numeração; armazenamento de XML e protocolos (5 anos).
- DANFE NFC-e com QR Code na impressora térmica.

**Aceite:** autorização, rejeição, timeout, reenvio sem duplicar e contingência demonstrados em homologação na UF escolhida.

Situação (14/10/2026):

- ✅ Documento fiscal separado da venda, numeração por série/ambiente sem colisão entre caixas, chave de acesso
  (inclusive com CNPJ alfanumérico), XML 4.00 assinado pela DFe.NET, QR Code v3, URL de consulta.
- ✅ Reenvio seguro: nota sem resposta é consultada pela chave antes de reenviar; duplicidade (204) vira consulta.
- ✅ Contingência offline (tpEmis 9) automática quando a SEFAZ não responde, com transmissão posterior.
- ✅ Rejeição mantém o número e permite corrigir e reenviar; denegação consome o número; cancelamento com prazo.
- ✅ DANFE NFC-e na impressora térmica, com QR Code; aba NFC-e na retaguarda; SEFAZ simulada para treino.
- ✅ Todos os cenários do aceite cobertos por testes automatizados contra SEFAZ simulada.
- ⏳ **Homologação real:** depende do certificado A1 da empresa, do credenciamento como emissor de NFC-e e do
  CSC de homologação na UF escolhida.
- ⏳ Inutilização de numeração; validação local pelos XSD oficiais; tributos aproximados (Lei 12.741/IBPT);
  grupos IBS/CBS da reforma tributária (avaliar obrigatoriedade por regime com o contador);
  CSOSN 101/201/202/203/900 e CST 10/20/30/51/70/90.

## Etapa 4 — Operação do dia a dia

- Trocar operador / bloquear tela sem fechar o sistema.
- Alterar quantidade de item, consulta de preço, devolução parcial e troca.
- Fornecedores na interface, entrada de compra com vários itens, importação do XML da NF-e do fornecedor.
- Estoque mínimo por produto e alerta.
- Relatórios: vendas por período, mais vendidos, margem, fechamento de caixa impresso, divergências por operador.

**Aceite:** um dia completo de loja (abertura, vendas, trocas, sangrias, fechamento, relatório) sem recorrer ao banco.

## Etapa 5 — Vários caixas e robustez

- Serviço central local (ASP.NET Core) com autenticação dos terminais; terminais deixam de acessar o banco direto.
- Controle de versão: terminais desatualizados não operam contra um banco mais novo.
- Backup automático agendado, retenção e cópia para outro local (pendrive/rede/nuvem); tela de restauração.
- Senha do banco protegida (DPAPI) na configuração da máquina.
- Testes de queda de rede/energia e reconexão.

**Aceite:** 3 caixas em rede, um deles desligado à força no meio da venda, sem venda duplicada nem perdida.

## Etapa 6 — Pagamentos integrados

- Pix com QR Code dinâmico e confirmação automática (API do PSP).
- TEF (SiTef, PayGo ou outro, conforme a adquirente dos pilotos) e registro de NSU/bandeira na NFC-e.

## Etapa 7 — Comercialização

- Licenciamento (ativação assinada, limites por plano, tolerância offline, transferência de máquina).
- Instalador testado em Windows limpo, incluindo instalação silenciosa do PostgreSQL.
- Atualização automática, manual de instalação/recuperação, exportação de logs para suporte.
- Testes com hardware real (leitor, impressora térmica, gaveta via ESC/POS).

## Etapa 8 — Pilotos

- 3 a 5 lojas reais; medir tempo de instalação, chamados e falhas que impedem vender; corrigir antes de ampliar.

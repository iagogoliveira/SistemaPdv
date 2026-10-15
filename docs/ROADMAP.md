# Roadmap até a operação em loja real

Decisões tomadas (14/10/2026):

- **Banco:** PostgreSQL (sem limite de tamanho, código aberto). Substitui o SQL Server Express antes de existirem dados reais.
- **Fiscal:** NFC-e com biblioteca própria em C# (DFe.NET / Zeus), sem custo por nota.
- **Regimes:** Simples Nacional (CSOSN) e regime normal (CST) desde o cadastro.
- **UF:** ainda não definida — tudo configurável por loja; a homologação começa quando a UF for escolhida.

Cada etapa termina com testes automatizados passando e um critério de aceite verificável.

## Pendências reservadas para depois (decisão de 14/10/2026)

Itens conhecidos que ficam fora das etapas em andamento e serão retomados antes da operação em produção:

**Dependem do cliente / contador**
- Escolha da UF, certificado A1, credenciamento NFC-e e CSC para homologar na SEFAZ real.
- Validação dos perfis tributários, NCMs e da aplicação de IBS/CBS (reforma tributária) por regime.
- Lojas para o piloto (3 a 5) e definição do segmento (impacta balança/produtos por peso).

**Pagamentos (complementos da Etapa 6)**
- Adaptador TEF real (SiTef ou PayGo, conforme a credenciadora) com homologação; parcelamento e pré-autorização.
- Homologação do Pix com o PSP da loja; webhook do PSP (exige endereço público) além da consulta periódica.
- Bandeiras além de Visa/Master/Amex/Sorocred/Diners na NFC-e (a biblioteca fiscal só tem essas: as demais saem como 99).

**Fiscal (complementos da Etapa 3)**
- Inutilização de numeração; validação local pelos XSD oficiais; tributos aproximados (Lei 12.741/IBPT).
- CSOSN 101/201/202/203/900 e CST 10/20/30/51/70/90; grupos IBS/CBS.
- NF-e de entrada para devoluções de venda com NFC-e (a devolução da Etapa 4 é só operacional: estoque e caixa).

**Infraestrutura (Etapas 6–7)**
- Instalador testado em Windows limpo (já compila; ainda sem teste numa máquina limpa); assinatura Authenticode do
  instalador e dos executáveis; atualização do caixa sem exigir administrador (serviço de atualização).
- Licenciamento online opcional (ativação/transferência pelo portal do fornecedor) — hoje é por arquivo assinado.
- Sessões de usuário emitidas pelo servidor (hoje ele confia no ID do usuário enviado por um caixa conectado) e HTTPS
  opcional entre caixa e servidor na rede da loja.
- Teste em rede real com 3 computadores (o aceite foi feito com 3 caixas no mesmo computador).
- ~~Desempenho em telas pequenas (1366×768) e com catálogo grande (meta de busca < 300 ms)~~ — feito em 15/10/2026
  (ver histórico de mudanças).

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

## Etapa 4 — Operação do dia a dia  ✅ concluída

- Trocar operador / bloquear tela sem fechar o sistema.
- Alterar quantidade de item, consulta de preço, devolução parcial e troca.
- Fornecedores na interface, entrada de compra com vários itens, importação do XML da NF-e do fornecedor.
- Estoque mínimo por produto e alerta.
- Relatórios: vendas por período, mais vendidos, margem, fechamento de caixa impresso, divergências por operador.

**Aceite:** um dia completo de loja (abertura, vendas, trocas, sangrias, fechamento, relatório) sem recorrer ao banco.

Feito: bloqueio/troca de operador (Ctrl+L), alterar quantidade (Ctrl+Q), consulta de preço (Ctrl+P), devolução parcial
com reembolso em dinheiro ou vale-troca (Ctrl+T) e venda paga com vale; fornecedores, entrada manual com vários itens e
importação do XML da NF-e (vínculo por código do fornecedor ou GTIN, fator de conversão, custo e NCM atualizados);
estoque mínimo com alerta; relatórios (vendas por dia, formas de pagamento, mais vendidos e margem, caixas, divergências
por operador, estoque abaixo do mínimo) com CSV e impressão A4; fechamento de caixa impresso. Aceite coberto por teste de
integração (dia completo) e percorrido pela interface.

## Etapa 5 — Vários caixas e robustez  ✅ concluída

- Serviço central local (ASP.NET Core) com autenticação dos terminais; terminais deixam de acessar o banco direto.
- Controle de versão: terminais desatualizados não operam contra um banco mais novo.
- Backup automático agendado, retenção e cópia para outro local (pendrive/rede/nuvem); tela de restauração.
- Senha do banco protegida (DPAPI) na configuração da máquina.
- Testes de queda de rede/energia e reconexão.

**Aceite:** 3 caixas em rede, um deles desligado à força no meio da venda, sem venda duplicada nem perdida.

Feito: `Pdv.Server` (ASP.NET Core, serviço do Windows) é o único com acesso ao banco e ao certificado; caixas conectados
por código e chave própria (hash no banco, DPAPI no caixa), revogáveis; um caixa não opera a gaveta de outro; mesma
versão obrigatória e banco "mais novo" recusado; diário local da venda conferido no servidor pela chave de idempotência;
backup diário com recuperação de horário perdido, retenção, segunda pasta, backup manual e restauração (com backup de
segurança e caixas mantidos conectados); senha do banco, código de conexão e NFC-e cifrados com DPAPI; fila da NFC-e no
servidor; instalador com tipo "computador principal" (serviço, firewall, ACL) e "somente caixa". Aceite coberto por
testes do servidor (3 caixas simultâneos com reenvio, conexão cortada no envio, diário) e percorrido pela interface com
3 caixas: caixa desligado com itens no carrinho e durante a finalização, diário de venda não enviada e já gravada,
servidor desligado e religado — 7 vendas, 7 chaves, 7 números, nenhuma duplicada.

## Etapa 6 — Pagamentos integrados  🟡 implementada; falta homologar com o PSP e a credenciadora reais

- Pix com QR Code dinâmico e confirmação automática (API do PSP).
- TEF (SiTef, PayGo ou outro, conforme a adquirente dos pilotos) e registro de NSU/bandeira na NFC-e.

Feito: **Pix dinâmico** pela API Pix padronizada pelo Banco Central (OAuth2 + certificado do PSP, `cob`, consulta,
remoção e devolução) com PSP simulado para treinamento; cobrança guardada no servidor, QR no caixa com confirmação
automática, acompanhamento de cobranças abertas pelo servidor, venda só aceita Pix confirmado e um Pix quita uma única
venda; conciliação na retaguarda (Pix pago sem venda ou de venda cancelada → devolução). **TEF** no caixa por uma porta
comum em três fases (aprovar, confirmar após gravar a venda, desfazer), com registro de pendências no disco (queda de
energia não deixa cliente cobrado sem venda), estorno ao cancelar a venda e TEF simulado; NSU, autorização, bandeira e
credenciadora gravados no pagamento, no comprovante e na **NFC-e** (grupo `card` com `tpIntegra = 1`; Pix dinâmico como
`tPag 17`). Percorrido pela interface: Pix pago automaticamente, crédito aprovado, débito negado, estorno no
cancelamento, cartão desfeito após o caixa ser desligado, Pix sem venda devolvido.

Falta (depende da loja): credenciais do PSP da loja para homologar o Pix real; escolha da credenciadora/TEF dos pilotos
para escrever o adaptador real (SiTef/CliSiTef ou PayGo/PGWebLib) e homologá-lo; validar na SEFAZ o grupo `card` do Pix
dinâmico (CNPJ do PSP).

## Etapa 7 — Comercialização  🟡 implementada; falta testar em Windows limpo e com hardware real

- Licenciamento (ativação assinada, limites por plano, tolerância offline, transferência de máquina).
- Instalador testado em Windows limpo, incluindo instalação silenciosa do PostgreSQL.
- Atualização automática, manual de instalação/recuperação, exportação de logs para suporte.
- Testes com hardware real (leitor, impressora térmica, gaveta via ESC/POS).

Feito: **licença** assinada (ECDSA) pelo fornecedor, presa ao computador principal, com plano, limite de caixas,
validade, demonstração de 30 dias (2 caixas), tolerância de 15 dias e modo restrito (vendas bloqueadas, dados
acessíveis); ferramenta `pdv-licenca` (chaves, emissão, conferência) e tela na retaguarda (situação, código da máquina,
instalação). **Atualização automática** dos caixas a partir do servidor (instalador guardado no servidor, SHA-256,
instalação silenciosa e reabertura). **PostgreSQL embutido** opcional no instalador com configuração silenciosa
(cluster UTF-8 só local, serviço, usuário e senhas aleatórias) — script testado; **pacote de logs para suporte**;
**ESC/POS** para abrir a gaveta e cortar o papel; manuais de licenciamento e de recuperação. Percorrido pela interface:
demonstração, bloqueio ao vencer, licença emitida pela ferramenta e venda liberada.

Instalador compilado (`SistemaPDV-Setup-0.7.0.exe`, 103 MB, com PostgreSQL 17.11 embutido; os binários embutidos
passaram pelo script de configuração e pelo `pg_dump`). Falta: testá-lo em Windows limpo, incluindo o PostgreSQL
embutido com os binários da EDB e a atualização silenciosa (exige senha de administrador no caixa); testar leitor,
impressora térmica, guilhotina e gaveta reais; assinatura digital (Authenticode) do instalador.

## Etapa 8 — Pilotos

- 3 a 5 lojas reais; medir tempo de instalação, chamados e falhas que impedem vender; corrigir antes de ampliar.

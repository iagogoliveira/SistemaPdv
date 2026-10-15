# Regras de negócio (domínio)

As regras ficam em `Pdv.Domain` (entidades) e são orquestradas pelos serviços de `Pdv.Application`.
Toda verificação de permissão é refeita no servidor a partir do banco — nunca a partir do que o caixa informa.

## Empresa, loja e terminais

- Uma empresa (`Company`) com dados fiscais (CNPJ numérico ou alfanumérico, IE, regime CRT 1–4, endereço com código
  IBGE) e uma loja (`Store`). Criadas automaticamente na primeira execução e editadas em Retaguarda › Empresa.
- Cada computador de caixa é um `Terminal` (código `001`, `002`…), registrado quando é conectado ao servidor. Guarda o
  hash da chave de acesso, quando foi conectado, o último contato e a versão do programa.

## Usuários e permissões

| Permissão | Código | Libera |
|---|---|---|
| Desconto | `sales.discount` | Desconto por item |
| Cancelamento | `sales.cancel` | Cancelar venda, devolução/troca |
| Caixa | `cash.manage` | Sangria e suprimento |
| Produtos | `catalog.manage` | Cadastro de produtos, fornecedores |
| Estoque | `inventory.adjust` | Entradas, ajustes, compras |
| Usuários | `users.manage` | Cadastro de usuários |
| Relatórios | `reports.view` | Relatórios |
| Empresa | `company.manage` | Empresa, tributação, NFC-e |
| Sistema | `system.manage` | Caixas conectados, código de conexão, backup e restauração |

Perfis iniciais: **Administrador** (todas; permissões novas são concedidas automaticamente ao atualizar),
**Supervisor** (desconto, cancelamento, caixa, relatórios) e **Operador** (nenhuma). Quando o operador não tem a
permissão, o caixa pede usuário e senha de um supervisor; o servidor valida a permissão de quem autorizou.

Senhas: PBKDF2-SHA256 com 210 mil iterações e sal aleatório.

## Produtos e estoque

- Produto com vários códigos de barras (GTIN validado), unidade, categoria, preço, custo, estoque mínimo e dados
  fiscais (NCM, CEST, origem, perfil tributário).
- **Estoque = soma de movimentações** (`StockMovement`): compra, venda, devolução de venda, ajuste por contagem
  (com motivo), cancelamento (estorno). Não existe campo de saldo editável.
- Estoque mínimo: produtos abaixo dele aparecem em vermelho na retaguarda e no relatório de reposição.

## Caixa

- No máximo **um caixa aberto por terminal** (índice único filtrado).
- Abertura com troco inicial; sangria e suprimento com motivo; cada venda em dinheiro lança entrada (líquida do troco).
- Fechamento: o operador informa o contado; o sistema mostra esperado, contado e diferença (falta/sobra) e oferece
  imprimir o relatório de fechamento.
- Concorrência: toda operação altera a revisão da sessão (`xmin`). Venda e fechamento simultâneos nunca são ambos
  gravados — o perdedor recebe uma mensagem e nada fica pela metade.

## Venda

- Itens guardam um **retrato** do produto (descrição, unidade, preço, custo e dados fiscais do momento).
- Arredondamento único (`Money.Round`: 2 casas, meio para cima) por linha.
- Pagamento dividido: dinheiro, Pix, débito, crédito e vale-troca; troco só sobre dinheiro.
- Desconto por item (valor ou %), liberado por quem tem `sales.discount`.
- CPF/CNPJ do consumidor opcional (vai na NFC-e).
- Finalização **atômica e idempotente** (ver [arquitetura](arquitetura.md#fluxo-de-uma-venda)); número sequencial
  global por sequência do PostgreSQL (sem conflito entre caixas).
- Cancelamento com motivo e supervisor: estorna estoque e dinheiro por lançamentos compensatórios; se houver NFC-e
  autorizada, ela é cancelada na SEFAZ antes.

## Devolução e troca

- Parcial, por item e quantidade, limitada ao que ainda não foi devolvido.
- Valor proporcional ao que foi pago na linha (com desconto); a última unidade devolvida leva o resto exato, para a soma
  das devoluções nunca passar do total da linha.
- Reembolso em **dinheiro** (exige caixa aberto com saldo) ou **vale-troca**.
- Vale-troca: código de 8 caracteres sem caracteres ambíguos (sem 0/O/1/I), validade de 90 dias, uso parcial, saldo
  baixado na mesma transação da venda e protegido contra uso simultâneo.
- O estoque volta automaticamente. (A NF-e de entrada da devolução está nas pendências fiscais.)

## Compras

- Fornecedores com CPF/CNPJ validado.
- Entrada manual com vários itens ou **importação do XML da NF-e** (modelo 55): valida modelo, chave e GTIN; recusa
  nota já lançada (chave única); avisa se o destinatário é outra empresa.
- Vínculo item do fornecedor → produto da loja aprendido (código do fornecedor ou GTIN) com **fator de conversão**
  (ex.: caixa com 12). Custo unitário = total da linha ÷ quantidade em estoque (4 casas); NCM preenchido se faltava.

## Auditoria

`AuditEntries` registra quem fez o quê (login, falhas, descontos, cancelamentos, caixa, estoque, compras, NFC-e,
conexão/desconexão de caixas, restauração de backup), separada dos logs técnicos.

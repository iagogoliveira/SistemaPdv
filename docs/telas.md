# Telas do sistema

Tour visual do Sistema PDV funcionando, capturado numa loja de demonstração (Papelaria Exemplo) com SEFAZ, Pix e TEF
**simulados**, certificado e licença de teste. Os dados são fictícios; as notas saem em homologação, sem valor fiscal.

As imagens ficam em [`docs/telas`](telas) e foram capturadas por automação (UI Automation), só das janelas do PDV, na versão 0.7.0 de 15/10/2026.

## Primeiro acesso

| | |
|---|---|
| ![Conexão com o servidor](telas/01-conexao-servidor.png) | **Conexão com o servidor.** Na primeira abertura, o caixa pergunta o endereço do servidor da loja. |
| ![Primeiro acesso](telas/02-primeiro-acesso.png) | **Criação do administrador.** Só na primeira vez da loja. |
| ![Login](telas/00-login.png) | **Entrada** dos operadores. |
| ![Abertura de caixa](telas/03-abertura-caixa.png) | **Abertura de caixa** com o troco inicial. |

## Retaguarda — implantação e cadastros

![Implantação no início](telas/10-implantacao-inicio.png)
**Implantação (início):** checklist do que falta para emitir NFC-e com segurança.

![Implantação em andamento](telas/24-implantacao-andamento.png)
**Implantação (depois da configuração):** empresa, certificado, CSC, IBPT, SEFAZ e licença prontos.

![Empresa](telas/11-empresa.png)
**Empresa:** dados fiscais do emitente (CNPJ, IE, regime, endereço com código IBGE).

![Tributação](telas/12-tributacao.png)
**Tributação:** perfis tributários e situação da tabela IBPT.

![Perfil tributário](telas/13-perfil-tributario.png)
**Novo perfil tributário:** CFOP, CSOSN/CST e PIS/COFINS.

![NFC-e](telas/14-nfce-configuracao.png)
**NFC-e:** emissão, ambiente, série, certificado A1 e CSC.

![Pagamentos](telas/15-pagamentos.png)
**Pagamentos:** Pix integrado (QR dinâmico) e TEF.

![Produtos](telas/16-produtos.png)
**Produtos:** cadastro com preço, custo, margem, estoque e situação fiscal.

![Cadastro de produto](telas/17-cadastro-produto.png)
**Cadastro de produto:** códigos de barras, unidade, NCM, CEST, origem e perfil.

![Histórico de estoque](telas/18-historico-estoque.png)
**Histórico de estoque** do produto.

![Compras](telas/19-compras.png)
**Compras:** fornecedores e entradas de mercadoria (manual ou pelo XML da NF-e).

![Novo fornecedor](telas/19a-novo-fornecedor.png)
**Novo fornecedor.**

![Entrada de mercadoria](telas/20-entrada-mercadoria.png)
**Entrada de mercadoria** com vários itens.

![Usuários](telas/21-usuarios.png)
**Usuários** e perfis de acesso.

![Novo usuário](telas/22-novo-usuario.png)
**Novo usuário.**

![Sistema](telas/23-sistema.png)
**Sistema:** licença, código de conexão, caixas conectados, backup e suporte.

## Frente de caixa

![Frente de caixa](telas/30-frente-de-caixa.png)
**Caixa livre**, com as teclas de atalho no rodapé.

| | |
|---|---|
| ![CPF na nota](telas/31-cpf-na-nota.png) | **CPF/CNPJ na nota** (F11). |
| ![Alterar quantidade](telas/32-alterar-quantidade.png) | **Alterar quantidade** do item (Ctrl+Q). |
| ![Desconto](telas/33-desconto.png) | **Desconto no item**, em valor ou percentual (F5). |
| ![Consulta de preço](telas/34-consulta-preco.png) | **Consulta de preço** (Ctrl+P)… |
| ![Resultado da consulta](telas/35-consulta-preco-resultado.png) | …e o resultado, sem entrar na venda. |

![Venda em andamento](telas/36-venda-em-andamento.png)
**Venda em andamento:** itens, desconto, CPF, total e formas de pagamento.

### Pagamentos

![Pix](telas/37-pagamento-pix.png)
**Pix com QR Code dinâmico:** a confirmação do banco chega sozinha.

![Cartão no TEF](telas/40-pagamento-cartao-tef.png)
**Cartão pelo TEF** (a imagem é da Etapa 6, com o mesmo diálogo).

| | |
|---|---|
| ![Dinheiro](telas/41-pagamento-dinheiro.png) | **Dinheiro**, com o valor recebido… |
| ![Troco](telas/42-troco.png) | …e o troco ao concluir. |

![Pagamento misto](telas/43-pagamento-misto.png)
**Pagamento misto:** parte em dinheiro, o restante no débito.

| | |
|---|---|
| ![Pergunta ao concluir](telas/38-venda-concluida-pergunta.png) | **Venda concluída:** pergunta se imprime. |
| ![Venda concluída](telas/39-venda-concluida.png) | Caixa livre de novo, com a NFC-e autorizada. |

### NFC-e, reimpressão e operações do caixa

| | |
|---|---|
| ![Reimprimir](telas/44-reimprimir.png) | **Reimprimir** (F8): escolha da venda. |
| ![DANFE](telas/45-danfe-nfce.png) | **DANFE NFC-e** com QR Code, chave de acesso, protocolo e tributos aproximados (IBPT). |
| ![Suprimento](telas/46-suprimento.png) | **Suprimento** (F3). |
| ![Sangria](telas/47-sangria.png) | **Sangria** (F4). |
| ![Devolução](telas/48-devolucao-busca.png) | **Devolução / troca** (Ctrl+T): número da venda… |
| ![Itens da devolução](telas/49-devolucao-itens.png) | …itens a devolver, em dinheiro ou vale-troca. |
| ![Cancelar venda](telas/50-cancelar-venda.png) | **Cancelar venda** (F7), que cancela também a NFC-e. |
| ![Caixa bloqueado](telas/51-caixa-bloqueado.png) | **Trocar operador / bloquear** (Ctrl+L) sem fechar o caixa. |

## Retaguarda depois das vendas

![Notas NFC-e](telas/60-nfce-notas.png)
**NFC-e:** notas autorizadas, pendentes e rejeitadas, com DANFE, reenvio, cancelamento e inutilização.

![Vendas por dia](telas/61-relatorio-vendas-por-dia.png)
**Relatório de vendas por dia.**

![Formas de pagamento](telas/62-relatorio-vendas-por-forma-de-pagamento.png)
**Vendas por forma de pagamento.**

![Mais vendidos](telas/63-relatorio-produtos-mais-vendidos.png)
**Produtos mais vendidos e margem.**

![Caixas](telas/64-relatorio-caixas-aberturas.png)
**Aberturas e fechamentos de caixa.**

![Estoque baixo](telas/65-relatorio-estoque-abaixo.png)
**Estoque abaixo do mínimo.**

![Conciliação Pix](telas/66-pagamentos-conciliacao.png)
**Conciliação do Pix** recebido.

![Produtos após vendas](telas/67-produtos-apos-vendas.png)
**Estoque atualizado** depois das vendas.

## Fechamento

| | |
|---|---|
| ![Fechamento](telas/70-fechamento-caixa.png) | **Fechamento de caixa** (F12) com o valor contado… |
| ![Resultado do fechamento](telas/71-fechamento-resultado.png) | …esperado, contado e diferença. |

![Caixa fechado](telas/72-caixa-fechado.png)
**Caixa fechado.**

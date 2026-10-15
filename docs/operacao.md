# Manual de operação

## Primeiro acesso

1. Abra o **Sistema PDV**. Na primeira vez em um computador ele pede o **servidor da loja** (`localhost` no computador
   principal) e, nos demais caixas, o **código de conexão** mostrado em Retaguarda › Sistema.
2. Na primeira vez da loja, a tela de entrada pede a criação do **administrador**. Depois ele cadastra os demais
   usuários em Retaguarda › Usuários.
3. Ao entrar com o caixa fechado, o sistema pede o troco inicial (abertura de caixa).

## Frente de caixa — atalhos

Todas as teclas também são botões na tela (mouse ou toque).

| Tecla | Ação |
|---|---|
| `Enter` | Adiciona item: código, `3*código` (3 unidades) ou parte da descrição |
| `F1` | Retaguarda (conforme permissão) |
| `F2` / `F12` | Abre / fecha o caixa (fechamento mostra falta ou sobra e oferece imprimir) |
| `F3` / `F4` | Suprimento / sangria (pede supervisor se necessário) |
| `F5` | Desconto no item selecionado: `2,50` ou `10%` |
| `F6` | Remove o item selecionado |
| `F7` | Cancela uma venda gravada (motivo + supervisor; estorna estoque e dinheiro; cancela a NFC-e) |
| `F8` | Reimprime / visualiza o comprovante |
| `F9` ou `Enter` no valor | Lança um pagamento (vários = pagamento dividido) |
| `F10` | Finaliza a venda |
| `F11` | CPF/CNPJ na nota (vazio remove) |
| `Ctrl+Q` | Altera a quantidade do item selecionado |
| `Ctrl+P` | Consulta preço e estoque sem lançar |
| `Ctrl+T` | Devolução / troca de uma venda |
| `Ctrl+L` | Bloqueia o caixa / troca de operador (o caixa continua aberto) |
| `Esc` | Descarta a venda em andamento |

Valor recebido vazio = valor que falta. O troco aparece em destaque e na mensagem de venda concluída.

### Pix e cartão integrados

- **Pix** (quando ativado em Retaguarda › Pagamentos): ao lançar, abre o QR Code com o valor; o cliente paga no app do
  banco e a janela fecha sozinha quando o PSP confirma. "Cancelar Pix" desiste (se o cliente pagou no último instante,
  o pagamento vale). QR vencido: "Gerar novo QR".
- **Cartão** (quando o caixa tem TEF): ao lançar crédito ou débito, o pinpad conduz a transação; aprovado, o pagamento
  entra com bandeira e NSU; negado, a mensagem aparece e outra forma pode ser usada. A via do cliente é impressa junto
  com o comprovante.
- Remover um pagamento com cartão desfaz a transação no TEF. Remover um Pix já recebido avisa que o valor deve ser
  devolvido em Retaguarda › Pagamentos.
- Se o caixa desligar com cartões aprovados e a venda não concluída, ao abrir de novo eles são **desfeitos** (o cliente
  não é cobrado) e o operador é avisado.
- Ao cancelar (F7) uma venda paga no TEF, o caixa oferece o **estorno** no cartão; paga com Pix, a devolução é feita na
  retaguarda.

### Situações especiais

- **"Não foi possível confirmar a venda com o servidor":** não altere a venda; pressione F10 de novo quando a conexão
  voltar — a venda não é duplicada.
- **Caixa desligou durante uma venda:** ao abrir de novo, o sistema avisa se a venda foi gravada ou pergunta se deve
  concluí-la (Sim) ou descartá-la (Não).
- **"Sem conexão com o servidor da loja":** verifique se o computador principal está ligado e na rede.

## Devolução e troca (Ctrl+T)

Informe o número da venda, a quantidade de cada item a devolver, a forma (vale-troca ou dinheiro) e o motivo. Exige
quem tenha permissão de cancelamento. O vale-troca impresso tem um código de 8 caracteres e vale 90 dias; na venda,
escolha a forma "Vale-troca" e digite o código (pode ser usado em parte).

## Retaguarda (F1)

| Aba | Permissão | O que faz |
|---|---|---|
| Produtos | produtos / estoque | Cadastro, códigos de barras, preço, custo, estoque mínimo, entradas, ajustes, histórico |
| Compras | produtos / estoque | Fornecedores, entrada manual com vários itens, importação do XML da NF-e |
| Relatórios | relatórios | Ver abaixo |
| Usuários | usuários | Usuários, perfis, senhas, ativação |
| Empresa | empresa | Dados fiscais da empresa e da loja |
| Tributação | empresa | Perfis tributários |
| NFC-e | empresa | Configuração da emissão, teste da SEFAZ, notas pendentes/rejeitadas, DANFE, cancelamento |
| Pagamentos | empresa / relatórios | Configuração do Pix integrado; Pix recebidos (30 dias), consulta ao PSP e devolução; TEF deste caixa |
| Sistema | sistema | Código de conexão, caixas conectados, backup e restauração |

## Relatórios

Vendas por dia; vendas por forma de pagamento (dinheiro líquido do troco); produtos mais vendidos e margem; caixas
(aberturas e fechamentos, com reimpressão do fechamento); divergências de caixa por operador; estoque abaixo do mínimo.
Todos com período (exceto reposição), exportação **CSV** (abre no Excel em português) e impressão A4.

## Backup

Retaguarda › Sistema:

- **Backup automático diário** no horário escolhido. Se o servidor estava desligado no horário, o backup é feito assim
  que ele liga.
- **Manter (dias):** backups mais antigos são apagados; os 3 mais recentes nunca são.
- **Segunda cópia:** pasta no computador do servidor — pendrive, pasta de rede (`\\NAS\pdv`) ou pasta sincronizada com a
  nuvem (OneDrive/Google Drive). A gravação é testada ao salvar; falhas aparecem em vermelho na tela.
- **Fazer backup agora** e **Restaurar selecionado**: digite RESTAURAR para confirmar. O banco volta ao estado do
  backup (vendas e cadastros posteriores deixam de existir), o estado atual é guardado num backup de segurança
  (`...-antes-de-restaurar.backup`) e os caixas continuam conectados. Depois, feche e abra o sistema nos caixas.

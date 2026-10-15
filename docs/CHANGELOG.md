# Histórico de mudanças

## 0.6.0 — Etapa 6: pagamentos integrados

- Pix com QR Code dinâmico pela API Pix do Banco Central (ou PSP simulado), confirmação automática e acompanhamento pelo
  servidor; venda só aceita Pix confirmado, que quita uma única venda.
- Retaguarda › Pagamentos: configuração do Pix (credenciais e certificado cifrados no servidor) e conciliação com
  consulta ao PSP e devolução de Pix pago sem venda ou de venda cancelada.
- TEF no caixa (porta comum, TEF simulado): aprovar → confirmar após gravar a venda → desfazer se não concluir;
  pendências no disco contra queda de energia; estorno ao cancelar a venda; via do cliente impressa com o comprovante.
- NSU, autorização, bandeira e credenciadora/PSP no pagamento, no comprovante e na NFC-e (`card` com `tpIntegra = 1`,
  Pix dinâmico `tPag 17`).
- Abas da retaguarda numa linha só ("Produtos").

## 0.5.0 — Etapa 5: vários caixas e robustez

- Servidor central da loja (`Pdv.Server`, serviço do Windows): único com acesso ao banco e ao certificado.
- Caixas conectados por código e chave própria; desconexão pela retaguarda; um caixa não opera outro.
- Mesma versão obrigatória entre caixa e servidor; servidor recusa banco de versão mais nova.
- Diário local da venda: recuperação após queda de energia sem duplicar nem perder.
- Backup diário com retenção e segunda pasta, backup manual e restauração pela retaguarda (aba Sistema).
- Senha do banco, código de conexão e NFC-e cifrados com DPAPI; configuração da NFC-e passa a ficar no servidor.
- Fila da NFC-e transmitida pelo servidor a cada 2 minutos.
- Instalador com "computador principal" (serviço, firewall, permissões) e "somente caixa".

## 0.4 — Etapa 4: operação do dia a dia

Troca de operador/bloqueio, alterar quantidade, consulta de preço, devolução parcial com vale-troca, fornecedores,
compras com vários itens e importação do XML da NF-e, estoque mínimo, relatórios com CSV e impressão, fechamento
impresso.

## 0.3 — Etapa 3: NFC-e em homologação

Emissão com DFe.NET, chave de acesso, consulta antes de reenviar, contingência offline, cancelamento, DANFE com QR Code,
configuração cifrada, SEFAZ simulada.

## 0.2 — Etapa 2: dados da empresa e cadastro fiscal

Empresa e loja na retaguarda, perfis tributários (CSOSN/CST, PIS/COFINS), NCM/CEST/origem no produto, retrato fiscal na
venda.

## 0.1 — Fundação

Camadas, PostgreSQL com migrações, autenticação e perfis, caixa e vendas (desconto com supervisor, pagamento dividido,
cancelamento, comprovante), produtos e estoque, interface com tema e ícones próprios, instalador.

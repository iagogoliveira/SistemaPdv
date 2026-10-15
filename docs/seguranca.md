# Segurança

## Princípios

- **Permissão sempre no servidor:** cada operação restrita consulta o banco pelo ID de quem fez/autorizou; a interface
  só decide quando pedir o supervisor.
- **Caixa sem segredos da loja:** o caixa não tem a senha do banco nem o certificado A1/CSC; tem apenas a própria chave
  de acesso ao servidor.
- **Menor exposição:** o PostgreSQL só aceita conexões locais do servidor da loja; caixas falam só com a porta 5080.

## Segredos

| Segredo | Onde fica | Proteção |
|---|---|---|
| Senha do banco | `server.json` (servidor) | DPAPI da máquina (cifrada pelo próprio servidor ao iniciar) |
| Código de conexão de caixas | `server.json` | DPAPI; comparação em tempo constante; 5 erros por endereço = bloqueio de 5 min |
| Certificado A1 | `certificado-a1.pfx` na pasta do servidor | Pasta só para Administradores/SYSTEM; senha cifrada |
| Senha do certificado e CSC | `nfce.json` | DPAPI; nunca enviados de volta ao caixa |
| Chave do caixa | `connection.json` (caixa) e SHA-256 no banco | DPAPI no caixa; o banco não guarda a chave |
| Credenciais do PSP (Pix) | `pix.json` e `certificado-psp.pfx` (servidor) | Client secret e senha do certificado com DPAPI; nunca enviados ao caixa |
| Senhas de usuários | Banco | PBKDF2-SHA256, 210 mil iterações, sal aleatório |
| Chave privada de licenças | Só com o fornecedor (`%USERPROFILE%\SistemaPDV-licencas`) | Nunca no repositório nem nas lojas (`*.pem` no `.gitignore`) |
| Senha do superusuário do PostgreSQL embutido | `server.json` | DPAPI; gerada aleatoriamente na instalação |

DPAPI no escopo da máquina: o arquivo copiado para outro computador não revela nada; por isso a pasta do servidor tem
permissão restrita (qualquer processo do próprio computador com acesso ao arquivo poderia decifrá-lo).

## Chaves dos caixas

256 bits aleatórios por caixa, trocadas a cada reconexão e revogáveis na retaguarda (o caixa precisa estar fechado). Um
caixa só pode operar o próprio caixa/gaveta: `terminalId` e `TerminalId` dos comandos são conferidos com a chave.

## Pagamentos

- A venda só aceita um Pix integrado **confirmado pelo PSP no servidor** — o caixa não consegue "declarar" um Pix pago.
- Dados de cartão: o PDV não lê nem guarda número de cartão (só NSU, autorização, bandeira e os últimos dígitos
  mascarados do comprovante); a leitura do cartão é do pinpad/TEF homologado.
- Devolução de Pix exige a permissão de cancelamento e motivo, e fica na auditoria.

## Licença e atualização

- A licença é conferida pela assinatura ECDSA com a chave pública embutida; alterar o arquivo (ex.: aumentar caixas)
  invalida a assinatura e coloca o sistema em modo restrito. O modo restrito nunca bloqueia acesso aos dados nem backup.
- O instalador baixado pelo caixa é conferido pelo SHA-256 informado pelo servidor e só é oferecido a caixas conectados.
  Pendente: assinatura Authenticode do instalador.
- O pacote de suporte não inclui senhas, chaves, certificados nem o código de conexão (verificado por teste).

## Auditoria e logs

Ações comerciais vão para `AuditEntries` (banco). Logs técnicos ficam em arquivo e não registram senhas, chaves nem o
código de conexão.

## Limitações conhecidas

- **Usuário informado pelo caixa:** o servidor confia no ID do usuário enviado por um caixa conectado (o login e a
  liberação do supervisor são validados, mas o ID não é amarrado a uma sessão). Um computador já conectado e
  adulterado poderia agir em nome de outro usuário. Planejado: sessões de usuário emitidas pelo servidor.
- **HTTP sem TLS na rede local:** a chave do caixa trafega em texto na rede da loja. Planejado: HTTPS opcional. Use rede
  separada para os caixas e Wi-Fi com senha forte.
- Dados de desenvolvimento (`pdv_dev`, `senha123`) existem só em configurações `Development` e testes.

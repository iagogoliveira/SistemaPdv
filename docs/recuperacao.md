# Manual de recuperação e suporte

## Pacote para o suporte

Retaguarda › Sistema › **Exportar logs para suporte** gera um `.zip` com os logs dos últimos 7 dias do servidor e do
caixa e um resumo técnico (versões, licença, caixas, backup, migrações). **Não** contém senhas, chaves, certificados
nem o código de conexão. Envie o arquivo ao suporte.

## Situações comuns

| Situação | O que fazer |
|---|---|
| Caixa: "Sem conexão com o servidor da loja" | Ligar o computador principal; conferir o serviço `SistemaPDV.Servidor` (`services.msc`); rede/cabo; porta 5080 no firewall |
| Caixa pede o código de conexão de novo | Foi desconectado, o banco foi restaurado de outra loja ou o `connection.json` foi apagado: digite o código de Retaguarda › Sistema |
| "Este caixa está na versão X e o servidor na Y" | Caixa mais antigo: aceite "Atualizar agora" (baixa do servidor). Servidor mais antigo: atualize o computador principal |
| "Vendas bloqueadas" | Licença vencida, demonstração encerrada ou licença de outro computador: instale a licença (ver [Licenciamento](licenciamento.md)) |
| Serviço do servidor não inicia | `%ProgramData%\SistemaPDV\Servidor\logs`: banco parado (`SistemaPDV-PostgreSQL` ou o PostgreSQL da máquina), senha do banco, banco de versão mais nova |
| Backup em vermelho na retaguarda | Segunda pasta inacessível (pendrive removido, rede fora). O backup local foi feito |
| Venda interrompida por queda de energia | Ao abrir, o caixa diz se a venda foi gravada ou pergunta se deve concluí-la; cartões sem venda são desfeitos sozinhos |
| Pix pago sem venda | Retaguarda › Pagamentos: aparece como "Pago — devolver"; use "Devolver Pix" |

## Restaurar um backup

Retaguarda › Sistema › selecione o backup › **Restaurar selecionado** › digite RESTAURAR. O estado atual é guardado
antes em `pdv-...-antes-de-restaurar.backup` (dá para desfazer restaurando-o). Depois, feche e abra o sistema nos caixas.

## Trocar o computador principal

1. No computador antigo: "Fazer backup agora" e copie o arquivo (`%ProgramData%\SistemaPDV\Servidor\backups`) para um
   pendrive. Anote/salve o certificado A1 e o CSC e as credenciais do Pix.
2. No computador novo: instale como "Computador principal"; em Retaguarda › Sistema copie o **código da máquina** e
   peça ao fornecedor a transferência da licença.
3. Copie o backup para a pasta `backups` do servidor novo e restaure-o pela retaguarda.
4. Informe de novo certificado/CSC (NFC-e) e credenciais do Pix — os segredos cifrados com DPAPI não passam de um
   computador para outro. Reconecte os caixas com o código de conexão.

## Banco de dados

- PostgreSQL embutido: serviço `SistemaPDV-PostgreSQL`, porta 5433, dados em `%ProgramData%\SistemaPDV\PostgreSQL`
  (mantidos na desinstalação). A senha do superusuário `postgres` fica cifrada (DPAPI) em `server.json`
  (`Postgres:AdminPassword`), para uso do suporte no próprio computador.
- Desinstalar o sistema **não apaga** banco, backups, licença nem configurações.

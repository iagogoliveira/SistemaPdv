# Documentação do Sistema PDV

Ponto de venda para lojas no Brasil, em Windows, com vários caixas em rede, NFC-e e retaguarda completa.
Esta pasta é a documentação oficial do projeto e é **atualizada junto com cada mudança** no código
(cada alteração de comportamento, configuração ou operação atualiza a página correspondente no mesmo commit).

| Página | Para quem | Conteúdo |
|---|---|---|
| [Visão geral e arquitetura](arquitetura.md) | Desenvolvimento | Projetos, camadas, fluxo de uma venda, decisões técnicas |
| [Regras de negócio (domínio)](dominio.md) | Desenvolvimento / suporte | Vendas, caixa, estoque, devoluções, vale-troca, compras, auditoria |
| [Servidor da loja e caixas em rede](servidor-e-caixas.md) | Desenvolvimento / suporte | Protocolo, conexão de caixas, versões, quedas de rede e energia |
| [Fiscal e NFC-e](fiscal.md) | Desenvolvimento / contador | Cadastro fiscal, emissão, contingência, cancelamento, SEFAZ simulada |
| [Manual de operação](operacao.md) | Operador / gerente | Atalhos do caixa, retaguarda, relatórios, backup |
| [Instalação e configuração](instalacao.md) | Técnico de implantação | Instalador, pré-requisitos, arquivos de configuração, firewall, atualização |
| [Segurança](seguranca.md) | Desenvolvimento / técnico | Permissões, segredos (DPAPI), chaves dos caixas, limitações conhecidas |
| [Licenciamento](licenciamento.md) | Fornecedor / técnico | Licença assinada, demonstração, limites, transferência, ferramenta `pdv-licenca` |
| [Recuperação e suporte](recuperacao.md) | Técnico / suporte | Pacote de logs, problemas comuns, restauração, troca do computador principal |
| [Desenvolvimento](desenvolvimento.md) | Desenvolvimento | Ambiente, execução, testes, migrações, convenções |
| [Roadmap](ROADMAP.md) | Todos | Etapas até a operação em loja real e pendências |
| [Histórico de mudanças](CHANGELOG.md) | Todos | O que mudou em cada versão |

Repositório: <https://github.com/iagogoliveira/SistemaPdv>

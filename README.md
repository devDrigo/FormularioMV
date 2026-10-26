# FluigPainel — visualização de chamados

Os itens da seção **Em Atendimento** abrem a tela nativa do chamado por meio de um proxy interno. O servidor autentica no Fluig e encaminha a página e seus recursos; as credenciais não são gravadas no código nem enviadas ao navegador.

Antes de executar a aplicação, configure no ambiente de execução:

```bash
FLUIG_VIEW_LOGIN=4200100
FLUIG_VIEW_PASSWORD='<senha do usuário técnico>'
```

Em Docker/Render, use as configurações de **Environment Variables/Secrets**. Não coloque a senha em `appsettings.json`, no Dockerfile, no repositório ou em parâmetros de URL.

O endpoint público usado pelos cartões é `/Home/VisualizarChamado?codigo=...`. Os recursos internos da tela são reescritos para `/Home/FluigProxy`, mantendo a sessão autenticada no servidor.

## Endpoint de funcionários (para a planilha do Google Sheets)

`GET /Home/Funcionarios?codFilial=35` consulta o dataset `ds_funcionario_dados_adicionais` do Fluig e devolve um JSON simples com os campos `chapa`, `funcionario`, `cpf`, `setor`, `situacao`, `dataAdmissao`, `dataDemissao`.

Existe porque o Google Apps Script (`UrlFetchApp`) não consegue negociar TLS diretamente com o servidor do Fluig. Este endpoint roda no servidor .NET, que já lida com essa conexão (reaproveita o login de `FLUIG_VIEW_LOGIN`/`FLUIG_VIEW_PASSWORD`), e devolve os dados por HTTPS normal para o Apps Script consumir.

Protegido por chave simples — configure `FUNCIONARIOS_API_KEY` nas variáveis de ambiente e envie o mesmo valor no header `X-Api-Key` em toda chamada.


## Automações executadas pelo servidor

O Apps Script mantém inventário, funcionários e estrutura da planilha. Ele apenas notifica o servidor sobre edições, sincronizações e horários; a avaliação das regras, abertura no Fluig e gravação do número do chamado são feitas pelo `AutomaticCallScheduler`. Configure `PLANILHA_SYNC_WEBAPP_URL` com a URL do Web App da planilha, `PLANILHA_SYNC_TOKEN` com o token aceito pelo script e `FUNCIONARIOS_API_KEY` com a mesma chave usada na ponte. O servidor sincroniza a planilha ao iniciar e não mantém polling contínuo.

# Arquitetura recomendada para automações

## Decisão

Automações que dependem de dados da planilha devem continuar sendo avaliadas pelo Apps Script, porque ele tem acesso direto às abas, células, fórmulas e gatilhos de sincronização.

Automações baseadas somente em tempo **não deveriam depender do Apps Script**. O componente correto para elas é um executor no servidor, com fila, histórico, tentativas, controle de duplicidade e observabilidade. Assim, uma rotina horária não fica sujeita à autorização, pausa, exclusão ou falha de gatilhos da planilha.

## Por que não foi ativado um executor de servidor nesta entrega

O projeto atual ainda não possui um serviço persistente de background nem uma fila durável. Adicionar apenas um `Timer` no processo web seria frágil em ambientes que reiniciam ou suspendem a aplicação e poderia abrir chamados duplicados. A separação precisa ser implementada junto com armazenamento durável, lock por execução e uma forma clara de montar o payload quando não existe uma linha da planilha.

## Próxima evolução segura

O modelo deve separar `Executor = Planilha` e `Executor = Servidor`. O executor de planilha continuará usando a regra e a coluna de retorno; o executor de servidor usará uma ação/template independente, com dados fixos ou variáveis próprias. O servidor poderá então executar os agendamentos sem depender da planilha, enquanto o Apps Script permanece responsável somente por eventos e dados originados no Google Sheets.

Nesta versão, a interface já diferencia claramente **Sincronização da planilha** de **Horário programado**, mas o agendamento por horário continua usando o executor existente do Apps Script para preservar a funcionalidade atual até que o executor de servidor seja implantado de forma segura.

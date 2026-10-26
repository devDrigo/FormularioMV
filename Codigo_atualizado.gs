/**
 * IMPORTAÇÃO DE FUNCIONÁRIOS VIA ENDPOINT DO PAINELTI35
 *
 * Estrutura da aba "Funcionários":
 * - Linha 1: orientações (editável).
 * - Linha 2: cabeçalhos.
 * - Linhas de dados (a partir da linha 3): uma mistura de
 *     - linhas MANUAIS (fundo amarelo) — adicionadas pelo menu Ação Manual >
 *       "Adicionar registro manual". NUNCA são apagadas ou alteradas pela
 *       atualização automática.
 *     - linhas do FLUIG — importadas e atualizadas automaticamente.
 *
 * As colunas CHAMADO_CRIACAO, CHAMADO_ALTERACAO e CHAMADO_DESLIGAMENTO
 * existem em TODAS as linhas (manuais ou do Fluig) e são sempre preenchidas
 * à mão. Elas nunca são apagadas: nas linhas manuais porque o script nunca
 * mexe nelas; nas linhas do Fluig porque o script guarda esses valores antes
 * de atualizar e devolve pra linha correspondente (casando pela CHAPA).
 *
 * Existe uma coluna extra, "ORIGEM" (coluna L), ESCONDIDA, que o script usa
 * internamente para saber se uma linha é manual ou do Fluig. Não mexa nela.
 *
 * CONFIGURAÇÃO:
 * 1. Ajuste URL_ENDPOINT_FUNCIONARIOS e CHAVE_API mais abaixo.
 * 2. Cole também o arquivo Formulario.html neste mesmo projeto do Apps
 *    Script (Arquivo > Novo > Arquivo HTML, nomeie exatamente "Formulario").
 * 3. Rode configurarPlanilha() uma vez e autorize os gatilhos da planilha.
 * 4. Rode atualizarFuncionarios() para testar a importação do Fluig.
 *    IMPORTANTE: confira o fuso horário do projeto em "Configurações do
 *    projeto" (ícone de engrenagem) — deve estar em America/Sao_Paulo,
 *    senão o gatilho roda às 7h de outro fuso.
 *
 * CHAMADOS:
 * - A avaliação e a abertura são feitas pelo programa C#.
 * - Este script apenas notifica alterações e faz a ponte de leitura/gravação.
 */

const URL_ENDPOINT_FUNCIONARIOS = 'https://painelti35.onrender.com/Home/Funcionarios?codFilial=35';
const CHAVE_API = '#sup@5162';

// A abertura de chamados e a identificação do solicitante ficam no programa C#.
// Este Apps Script mantém somente planilha, inventário, funcionários e ponte.
const URL_PROGRAMA_AUTOMACOES = URL_ENDPOINT_FUNCIONARIOS.split('/Home/')[0] + '/Home/NotificarAutomacao';
function respostaJson_(objeto) { return ContentService.createTextOutput(JSON.stringify(objeto)).setMimeType(ContentService.MimeType.JSON); }
function tokenPonte_() { return PropertiesService.getScriptProperties().getProperty('FUNCIONARIOS_API_KEY') || PropertiesService.getScriptProperties().getProperty('PAINEL_SYNC_TOKEN') || CHAVE_API; }
function notificarProgramaAutomacao_(evento) { try { const resposta=UrlFetchApp.fetch(URL_PROGRAMA_AUTOMACOES,{method:'post',contentType:'application/json',muteHttpExceptions:true,payload:JSON.stringify(evento||{}),headers:{'X-Api-Key':tokenPonte_()}}); if(resposta.getResponseCode()<200||resposta.getResponseCode()>=300) console.log('Programa não recebeu evento: HTTP '+resposta.getResponseCode()); return resposta.getResponseCode()>=200&&resposta.getResponseCode()<300; } catch(erro){ console.log('Falha ao notificar programa: '+erro); return false; } }
function notificarEdicaoPlanilha_(e) { if(!e||!e.range)return; const r=e.range,s=r.getSheet(); notificarProgramaAutomacao_({tipo:'edicao',aba:s.getName(),linhaInicial:r.getRow(),quantidadeLinhas:r.getNumRows(),colunaInicial:r.getColumn(),quantidadeColunas:r.getNumColumns(),ocorridoEm:new Date().toISOString()}); }
function notificarGatilhoHorario_() { notificarProgramaAutomacao_({tipo:'gatilho_horario',ocorridoEm:new Date().toISOString()}); }

function obterUrlWebAppInventario_() { return URL_WEBAPP_INVENTARIO_OFICIAL; }

const URL_WEBAPP_INVENTARIO_OFICIAL = 'https://script.google.com/macros/s/AKfycbx9Z3tQSRpLdvRUrals1BcXytHGh3SqKXpZebRuMaOPkE_3FLaolDeQCZJAzlZqFDMmRA/exec';

const NOME_ABA = 'Funcionários';
const LINHA_ORIENTACOES = 1;
const LINHA_TITULOS = 2;
const PRIMEIRA_LINHA_DADOS = 3;

// Índices de coluna (1 = A, 2 = B, ...)
const COL_CHAPA = 1;
const COL_FUNCIONARIO = 2;
const COL_CPF = 3;
const COL_FUNCAO = 4;
const COL_SETOR = 5;
const COL_SITUACAO = 6;
const COL_DATAADMISSAO = 7;
const COL_DATADEMISSAO = 8;
const COL_CHAMADO_CRIACAO = 9;
const COL_CHAMADO_ALTERACAO = 10;
const COL_CHAMADO_DESLIGAMENTO = 11;
const COL_ORIGEM = 12; // coluna oculta, uso interno

const TOTAL_COLUNAS = 12;
const NOME_ABA_CONFIGURACAO = '_CONFIG_AUTOMACOES';
const VERSAO_CARGA_INICIAL = 'v8';

const CABECALHOS = [
  'CHAPA ou TIPO', 'NOME', 'CPF', 'FUNCAO', 'SETOR', 'SITUACAO',
  'ADMISSAO', 'DEMISSAO',
  'CHAMADO_CRIACAO', 'CHAMADO_ALTERACAO', 'CHAMADO_DESLIGAMENTO',
  'ORIGEM'
];

const COR_MANUAL = '#fff2cc';
const ORIGEM_MANUAL = 'MANUAL';
const ORIGEM_FLUIG = 'FLUIG';

// ============================================================================
// PONTE PARA O PAINEL — publique este projeto como Web App
// ============================================================================
// Implantar > Nova implantação > Aplicativo da Web > Executar como você >
// Quem tem acesso: qualquer pessoa com o link. Use um token em Propriedades
// do script chamado PAINEL_SYNC_TOKEN. O painel chama esta função sozinho.
function doPost(e) {
  try {
    const tokenEsperado = PropertiesService.getScriptProperties().getProperty('PAINEL_SYNC_TOKEN') || PropertiesService.getScriptProperties().getProperty('FUNCIONARIOS_API_KEY') || CHAVE_API;
    const token = String((e && e.parameter && e.parameter.token) || '');
    if (tokenEsperado && token !== tokenEsperado) return respostaJson_({success:false,error:'unauthorized'});
    const acao=String((e&&e.parameter&&e.parameter.acao)||'');
    if(acao==='gravar_chamado') { const p=JSON.parse((e&&e.postData&&e.postData.contents)||'{}'); const sh=SpreadsheetApp.getActive().getSheetByName(String(p.aba||'')); if(!sh)return respostaJson_({success:false,error:'sheet_not_found'}); const l=Number(p.linha||0),c=Number(p.coluna||0); if(l<1||c<1)return respostaJson_({success:false,error:'invalid_cell'}); sh.getRange(l,c).setValue(String(p.numeroChamado||'')); SpreadsheetApp.flush(); return respostaJson_({success:true,aba:sh.getName(),linha:l,coluna:c,numeroChamado:String(p.numeroChamado||'')}); }
    if(acao==='notificar_horario'){notificarGatilhoHorario_();return respostaJson_({success:true});}
    if(acao!=='publicar') return respostaJson_({success:false,error:'write_requires_explicit_publication'});
    const store=JSON.parse((e&&e.postData&&e.postData.contents)||'{}'), anterior=lerBackupStore_();
    if(anterior&&(Array.isArray(anterior.Configuracoes)&&anterior.Configuracoes.length||Array.isArray(anterior.Regras)&&anterior.Regras.length)&&(!store||!Array.isArray(store.Configuracoes)||!store.Configuracoes.length)&&(!store||!Array.isArray(store.Regras)||!store.Regras.length)) return respostaJson_({success:false,error:'empty_store_would_overwrite_existing_config'});
    salvarBackupStore_(store); return respostaJson_({success:true,configuracoes:(store.Configuracoes||[]).length,regras:(store.Regras||[]).length,fontes:(store.Fontes||[]).length,exclusoes:(store.Exclusoes||[]).length,salvoEm:new Date().toISOString()});
  } catch(erro){return respostaJson_({success:false,error:String(erro)});}
}
function doGet(e) {
  try {
    const acaoInicial = String((e && e.parameter && e.parameter.acao) || '');
    if (acaoInicial === 'inventario') {
      limparEstadoOrfaoInventario_();
      const ativoInventario = PropertiesService.getDocumentProperties().getProperty('INVENTARIO_EM_ANDAMENTO');
      const tokenInventario = String((e && e.parameter && e.parameter.token) || '');
      const esperadoInventario = PropertiesService.getDocumentProperties().getProperty('INVENTARIO_TOKEN') || '';
      if (!ativoInventario) return HtmlService.createHtmlOutput('<h2>Este inventário já foi finalizado.</h2>');
      // O token continua aceito quando presente, mas o link oficial usa apenas
      // a URL estável da implantação enquanto o inventário estiver ativo.
      if (tokenInventario && esperadoInventario && tokenInventario !== esperadoInventario) return HtmlService.createHtmlOutput('<h2>Link de inventário inválido ou expirado.</h2>');
      const pagina = HtmlService.createTemplateFromFile('InventarioWebApp');
      const opcoes = obterOpcoesInventario();
      pagina.opcoesJson = JSON.stringify(opcoes).replace(/</g, '\u003c');
      pagina.sessaoJson = JSON.stringify(obterRegistrosInventarioSessao()).replace(/</g, '\u003c');
      return pagina.evaluate().setTitle('Inventário de equipamentos').setXFrameOptionsMode(HtmlService.XFrameOptionsMode.ALLOWALL);
    }
    const tokenEsperado = PropertiesService.getScriptProperties().getProperty('PAINEL_SYNC_TOKEN') || CHAVE_API;
    const token = String((e && e.parameter && e.parameter.token) || '');
    if (tokenEsperado && token !== tokenEsperado) return ContentService.createTextOutput(JSON.stringify({ success: false, error: 'unauthorized' })).setMimeType(ContentService.MimeType.JSON);
    const acao = String((e && e.parameter && e.parameter.acao) || 'status');
    if(acao==='ler_aba'){const nome=String((e&&e.parameter&&e.parameter.aba)||'');const sh=SpreadsheetApp.getActive().getSheetByName(nome);if(!sh)return respostaJson_({success:false,error:'sheet_not_found'});const inicio=nome===NOME_ABA?PRIMEIRA_LINHA_DADOS:2,cab=nome===NOME_ABA?LINHA_TITULOS:1,ult=sh.getLastRow(),col=Math.max(sh.getLastColumn(),1);return respostaJson_({success:true,aba:nome,linhaCabecalho:cab,primeiraLinhaDados:inicio,cabecalhos:sh.getRange(cab,1,1,col).getDisplayValues()[0],linhas:ult>=inicio?sh.getRange(inicio,1,ult-inicio+1,col).getDisplayValues():[],lidoEm:new Date().toISOString()});}
    if (acao === 'sincronizar') { var storePersistido = lerBackupStore_(); return ContentService.createTextOutput(JSON.stringify({ success: true, acao: acao, nome: SpreadsheetApp.getActive().getName(), endereco: SpreadsheetApp.getActive().getUrl(), abas: SpreadsheetApp.getActive().getSheets().map(function (x) { return x.getName(); }), store: storePersistido, executadoEm: new Date().toISOString() })).setMimeType(ContentService.MimeType.JSON); }
    const ss = SpreadsheetApp.getActive();
    return ContentService.createTextOutput(JSON.stringify({ success: true, acao: acao, nome: ss.getName(), endereco: ss.getUrl(), abas: ss.getSheets().map(function (x) { return x.getName(); }), store: lerBackupStore_(), executadoEm: new Date().toISOString() })).setMimeType(ContentService.MimeType.JSON);
  } catch (erro) {
    return ContentService.createTextOutput(JSON.stringify({ success: false, error: String(erro && erro.stack || erro) })).setMimeType(ContentService.MimeType.JSON);
  }
}

// ============================================================================
// MENU
// ============================================================================

function garantirAbaConfiguracao_() {
  const ss = SpreadsheetApp.getActive();
  let aba = ss.getSheetByName(NOME_ABA_CONFIGURACAO);
  if (!aba) { aba = ss.insertSheet(NOME_ABA_CONFIGURACAO); aba.getRange('A1').setValue('Configurações gerenciadas pela Central de Chamados'); aba.hideSheet(); }
  return aba;
}

function reforcarBloqueioInventarioAberto_() {
  const props=PropertiesService.getDocumentProperties(); if (!props.getProperty('INVENTARIO_EM_ANDAMENTO')) return;
  const ss=SpreadsheetApp.getActive(), principal=ss.getSheetByName(NOME_ABA_INVENTARIO), temp=ss.getSheetByName(NOME_ABA_INVENTARIO_TEMP);
  if (!principal || !temp) return;
  const atual=ss.getActiveSheet(); if (atual && (atual.getName()===NOME_ABA_INVENTARIO || atual.getName()===NOME_ABA_INVENTARIO_TEMP)) ativarAbaNeutraDuranteInventario_(ss);
  [principal,temp].forEach(function(sheet){ sheet.getProtections(SpreadsheetApp.ProtectionType.SHEET).forEach(function(x){ if (x.getDescription()===DESCRICAO_PROTECAO_INVENTARIO || x.getDescription()==='PROTECAO_INVENTARIO_TEMP') x.setWarningOnly(false); }); });
  try { principal.hideSheet(); temp.hideSheet(); } catch(e) {}
}

function onOpen() {
  try { reforcarBloqueioInventarioAberto_(); } catch (e) { console.log('Bloqueio do inventário aguardando autorização: ' + e); }
  try { garantirAbaConfiguracao_(); } catch (e) { console.log(e); }
  SpreadsheetApp.getUi()
    .createMenu('Ação Manual')
    .addItem('Configurar estrutura da planilha', 'configurarPlanilha')
    .addItem('Atualizar dados dos colaboradores', 'atualizarFuncionarios')
    .addSeparator()
    .addItem('Inserir colaborador manualmente', 'abrirFormularioManual')
    .addSeparator()
    .addItem('Iniciar inventário de equipamentos', 'iniciarInventarioEquipamentos')
    .addToUi();
  if (PropertiesService.getDocumentProperties().getProperty('INVENTARIO_EM_ANDAMENTO')) {
    try { SpreadsheetApp.getUi().alert('INVENTÁRIO EM ANDAMENTO', 'Não altere Tipo, Modelo, Local, Alugado, Patrimônio ou Status nas abas Equipamentos e Inventário em andamento.\n\nAs colunas após F continuam disponíveis para preventivos, chamados e informações de manutenção.\n\nUse o formulário do inventário para cadastrar ou atualizar equipamentos.', SpreadsheetApp.getUi().ButtonSet.OK); } catch (erro) { mostrarMensagem_('INVENTÁRIO EM ANDAMENTO: use o formulário. As colunas após F continuam disponíveis para manutenção.'); }
  }
}

// ============================================================================
// CONFIGURAÇÃO INICIAL DA PLANILHA
// ============================================================================
// Mostra alertas quando possível, mas não interrompe a execução em
// gatilhos ou em projetos executados sem uma interface aberta.
function mostrarMensagem_(mensagem) {
  try {
    SpreadsheetApp.getUi().alert(mensagem);
  } catch (erro) {
    const ss = SpreadsheetApp.getActive();
    if (ss) ss.toast(mensagem, 'Painel TI', 8);
    console.log(mensagem);
  }
}

// Garante que a planilha consiga ser recriada mesmo depois de apagar
// linhas, colunas ou todo o conteúdo da aba.
function garantirEstruturaMinima_(sheet) {
  if (sheet.getMaxRows() < TOTAL_COLUNAS) {
    sheet.insertRowsAfter(sheet.getMaxRows(), TOTAL_COLUNAS - sheet.getMaxRows());
  }
  if (sheet.getMaxColumns() < TOTAL_COLUNAS) {
    sheet.insertColumnsAfter(sheet.getMaxColumns(), TOTAL_COLUNAS - sheet.getMaxColumns());
  }
  if (sheet.getMaxRows() < PRIMEIRA_LINHA_DADOS) {
    sheet.insertRowsAfter(sheet.getMaxRows(), PRIMEIRA_LINHA_DADOS - sheet.getMaxRows());
  }
}


function configurarPlanilha() {
  garantirAbaConfiguracao_();
  const ss = SpreadsheetApp.getActive();
  let sheet = ss.getSheetByName(NOME_ABA);
  if (!sheet) sheet = ss.insertSheet(NOME_ABA);
  garantirEstruturaMinima_(sheet);

  // Linha 1: orientações
  sheet.getRange(LINHA_ORIENTACOES, 1, 1, TOTAL_COLUNAS - 1).merge();
  if (!sheet.getRange(LINHA_ORIENTACOES, 1).getValue()) {
    sheet.getRange(LINHA_ORIENTACOES, 1).setValue(
      'Orientações: linhas amarelas são manuais (adicionadas pelo menu Ação Manual > ' +
      'Adicionar registro manual) e nunca são apagadas pela atualização automática. ' +
      'As colunas de chamado (criação/alteração/desligamento) devem sempre ser preenchidas ' +
      'quando houver criação, alteração ou desligamento do colaborador.'
    );
  }
  sheet.getRange(LINHA_ORIENTACOES, 1, 1, TOTAL_COLUNAS - 1)
    .setBackground('#d9eaf7').setFontWeight('bold').setWrap(true);

  // Linha 2: cabeçalhos
  sheet.getRange(LINHA_TITULOS, 1, 1, TOTAL_COLUNAS).setValues([CABECALHOS]);
  sheet.getRange(LINHA_TITULOS, 1, 1, TOTAL_COLUNAS)
    .setBackground('#1f4e78').setFontColor('#ffffff').setFontWeight('bold');
  sheet.setFrozenRows(LINHA_TITULOS);

  // Coluna de origem fica escondida — é só controle interno do script
  sheet.hideColumns(COL_ORIGEM);

  // Migração: se já existirem linhas de dados sem a coluna ORIGEM preenchida
  // (ex.: planilha de uma versão anterior deste script), tenta inferir pela
  // cor de fundo da célula A da linha.
  const ultimaLinha = sheet.getLastRow();
  if (ultimaLinha >= PRIMEIRA_LINHA_DADOS) {
    const quantidade = ultimaLinha - PRIMEIRA_LINHA_DADOS + 1;
    const origemRange = sheet.getRange(PRIMEIRA_LINHA_DADOS, COL_ORIGEM, quantidade, 1);
    const origens = origemRange.getValues();
    const cores = sheet.getRange(PRIMEIRA_LINHA_DADOS, 1, quantidade, 1).getBackgrounds();

    for (let i = 0; i < origens.length; i++) {
      if (!origens[i][0]) {
        const corAtual = (cores[i][0] || '').toLowerCase();
        origens[i][0] = (corAtual === COR_MANUAL.toLowerCase()) ? ORIGEM_MANUAL : ORIGEM_FLUIG;
      }
    }
    origemRange.setValues(origens);
  }

  // Formata coluna CPF como texto, pra não perder zero à esquerda
  sheet.getRange(PRIMEIRA_LINHA_DADOS, COL_CPF, Math.max(sheet.getMaxRows() - PRIMEIRA_LINHA_DADOS + 1, 1), 1)
    .setNumberFormat('@');

  aplicarFiltro_(sheet);
  ajustarLargurasColunas_(sheet);

  // Atualização automática diária (~6h) já entra ativa — sem precisar de menu.
  try { ativarAtualizacaoDiaria(); }
  catch (erro) { console.log('Gatilhos automáticos serão criados na primeira atualização: ' + erro); }

  mostrarMensagem_('Estrutura configurada. Atualização automática diária (~6h) já está ativa.\n\n' +
    'Use o menu Ação Manual > "Adicionar registro manual" para incluir alguém que ' +
    'não vem do Fluig (PJ, estatutário, etc).');
}

// ============================================================================
// ATUALIZAÇÃO AUTOMÁTICA A PARTIR DO FLUIG
// ============================================================================

function sincronizarPlanilhaComPainel(pularPublicacaoBackup) {
  const url = URL_ENDPOINT_FUNCIONARIOS.split('/Home/')[0] + '/Home/SincronizarPlanilha';
  const ss = SpreadsheetApp.getActive();
  const abas = ss.getSheets().map(function (sheet) { return sheet.getName(); });
  const payload = { nome: ss.getName(), endereco: ss.getUrl(), abas: abas };
  const chave = PropertiesService.getScriptProperties().getProperty('FUNCIONARIOS_API_KEY') || CHAVE_API;
  const resposta = UrlFetchApp.fetch(url, { method: 'post', contentType: 'application/json', payload: JSON.stringify(payload), muteHttpExceptions: true, headers: { 'X-Api-Key': chave } });
  if (resposta.getResponseCode() < 200 || resposta.getResponseCode() >= 300) throw new Error('Falha ao sincronizar a planilha com o painel. HTTP ' + resposta.getResponseCode() + ': ' + resposta.getContentText());
}

function ativarSincronizacaoPlanilha() {
  ScriptApp.getProjectTriggers().forEach(function (trigger) { if (trigger.getHandlerFunction() === 'sincronizarPlanilhaComPainel') ScriptApp.deleteTrigger(trigger); });
  ScriptApp.newTrigger('sincronizarPlanilhaComPainel').forSpreadsheet(SpreadsheetApp.getActive()).onChange().create();
  sincronizarPlanilhaComPainel();
}

function diagnosticarHorarioAtual() {
  const agora = new Date();
  const mensagem = 'Horário considerado pelo Apps Script: ' + Utilities.formatDate(agora, 'America/Sao_Paulo', 'dd/MM/yyyy HH:mm:ss') +
    '\nFuso do projeto: ' + (Session.getScriptTimeZone() || 'não configurado') +
    '\nHorário do sistema: ' + agora.toString();
  mostrarMensagem_(mensagem);
  console.log(mensagem);
}

function atualizarFuncionarios() {
  try {
    atualizarFuncionariosCore_();
    removerGatilhosDeRetentativa_();
  } catch (erro) {
    garantirGatilhoDeRetentativa_();
    throw erro;
  }
}

function normalizarChapa_(valor) {
  const texto = String(valor == null ? '' : valor).trim();
  if (!texto) return '';
  if (/^\d+$/.test(texto)) return String(Number(texto));
  return texto.toUpperCase();
}

function atualizarFuncionariosCore_(pularPublicacaoBackup, pularAutomacoes) {
  const ss = SpreadsheetApp.getActive();
  garantirGatilhoDeSincronizacao_();
  try { ativarAtualizacaoDiaria(); }
  catch (erro) { console.log('Não foi possível reforçar os gatilhos automáticos: ' + erro); }

  sincronizarPlanilhaComPainel(!!pularPublicacaoBackup);
  const sheet = ss.getSheetByName(NOME_ABA);
  if (!sheet) throw new Error('A aba "' + NOME_ABA + '" não existe. Rode "Configurar estrutura da planilha".');
  garantirEstruturaMinima_(sheet);

  const ultimaLinha = sheet.getLastRow();
  const ticketsPorChapa = {}; // CHAPA -> { criacao, alteracao, desligamento }
  let primeiraLinhaFluig = null; // primeira linha não-manual encontrada
  let ultimaLinhaFluigAntiga = null; // última linha não-manual encontrada

  if (ultimaLinha >= PRIMEIRA_LINHA_DADOS) {
    const quantidade = ultimaLinha - PRIMEIRA_LINHA_DADOS + 1;
    const dados = sheet.getRange(PRIMEIRA_LINHA_DADOS, 1, quantidade, TOTAL_COLUNAS).getValues();

    for (let i = 0; i < dados.length; i++) {
      const linha = dados[i];
      const origem = linha[COL_ORIGEM - 1];
      const numeroDaLinha = PRIMEIRA_LINHA_DADOS + i;

      if (origem === ORIGEM_MANUAL) {
        continue; // nunca mexe em linha manual
      }

      // Linha do Fluig (ou sem marcação — trata como Fluig por segurança)
      const chapa = normalizarChapa_(linha[COL_CHAPA - 1]);
      if (chapa) {
        ticketsPorChapa[chapa] = {
          criacao: linha[COL_CHAMADO_CRIACAO - 1] || '',
          alteracao: linha[COL_CHAMADO_ALTERACAO - 1] || '',
          desligamento: linha[COL_CHAMADO_DESLIGAMENTO - 1] || '',
        };
      }
      if (primeiraLinhaFluig === null) primeiraLinhaFluig = numeroDaLinha;
      ultimaLinhaFluigAntiga = numeroDaLinha;
    }
  }

  // Consulta e valida antes de tocar na planilha. Nunca limpe as linhas
  // atuais se o endpoint retornar vazio ou uma carga claramente incompleta:
  // isso preserva os números dos chamados durante indisponibilidade do Fluig.
  const registros = buscarFuncionariosNoEndpoint_();
  if (!Array.isArray(registros) || registros.length === 0) {
    throw new Error('O endpoint do Fluig retornou zero funcionários; atualização cancelada para preservar as colunas de chamados.');
  }
  const linhasAtuais = primeiraLinhaFluig === null ? 0 : (ultimaLinhaFluigAntiga - primeiraLinhaFluig + 1);
  if (linhasAtuais >= 10 && registros.length < Math.floor(linhasAtuais * 0.5)) {
    throw new Error('O endpoint do Fluig retornou uma carga muito menor que a atual (' + registros.length + ' de ' + linhasAtuais + '); atualização cancelada para preservar as colunas de chamados.');
  }

  // Só LIMPA o conteúdo das linhas antigas depois da validação acima.
  // Usar sheet.deleteRow() aqui reduziria o total de linhas da planilha.
  if (primeiraLinhaFluig !== null) {
    sheet.getRange(primeiraLinhaFluig, 1, ultimaLinhaFluigAntiga - primeiraLinhaFluig + 1, TOTAL_COLUNAS)
      .clearContent();
  }

  // Traz sempre em ordem crescente de chapa
  registros.sort(function (a, b) {
    const chapaA = parseInt(String(a.chapa || '').replace(/\D/g, ''), 10);
    const chapaB = parseInt(String(b.chapa || '').replace(/\D/g, ''), 10);
    if (isNaN(chapaA) && isNaN(chapaB)) return 0;
    if (isNaN(chapaA)) return 1;
    if (isNaN(chapaB)) return -1;
    return chapaA - chapaB;
  });

  if (registros.length > 0) {
    // Onde a área do Fluig começa: logo depois do bloco manual (se existir
    // um bloco antigo do Fluig, usa a mesma posição; senão, depois da
    // última linha ocupada).
    const linhaDestino = primeiraLinhaFluig !== null
      ? primeiraLinhaFluig
      : Math.max(sheet.getLastRow() + 1, PRIMEIRA_LINHA_DADOS);

    // Garante que existam linhas suficientes na planilha antes de escrever
    // (senão getRange() lança "linhas fora dos limites").
    const linhasNecessarias = (linhaDestino + registros.length - 1) - sheet.getMaxRows();
    if (linhasNecessarias > 0) {
      sheet.insertRowsAfter(sheet.getMaxRows(), linhasNecessarias);
    }

    const values = registros.map(function (item) {
      const chapa = item.chapa || '';
      const tickets = ticketsPorChapa[normalizarChapa_(chapa)] || { criacao: '', alteracao: '', desligamento: '' };
      return [
        chapa,
        item.funcionario || '',
        item.cpf || '',
        item.funcao || '',
        item.setor || '',
        item.situacao || '',
        converterData_(item.dataAdmissao),
        converterData_(item.dataDemissao),
        tickets.criacao,
        tickets.alteracao,
        tickets.desligamento,
        ORIGEM_FLUIG,
      ];
    });

    const destino = sheet.getRange(linhaDestino, 1, values.length, TOTAL_COLUNAS);
    destino.setValues(values);
    destino.setBackground(null); // garante que não fique amarelo por engano
    sheet.getRange(linhaDestino, COL_CPF, values.length, 1).setNumberFormat('@');
    sheet.getRange(linhaDestino, COL_CHAPA, values.length, 1).setNumberFormat('@');
    sheet.getRange(linhaDestino, COL_DATAADMISSAO, values.length, 2).setNumberFormat('dd/mm/yyyy');
  }

  aplicarFiltro_(sheet);
  ajustarLargurasColunas_(sheet);

  // A automação é avaliada pelo programa C#; aqui só notificamos a carga concluída.
  notificarProgramaAutomacao_({tipo:'sincronizacao',abas:[NOME_ABA],quantidadeAlterada:registros.length,ocorridoEm:new Date().toISOString()});

  SpreadsheetApp.getActive().toast(registros.length + ' registros do Fluig atualizados.', 'Fluig', 5);
}

/**
 * Busca no painel TODAS as automações de chamado ativas configuradas na
 * central (agendamentos), já resolvidas com a aba, as condições da regra
 * associada, o texto de título/descrição e a coluna onde o número do
 * chamado deve ser gravado. Isso é o que permite que qualquer regra criada
 * no painel passe a funcionar de verdade, sem precisar mexer neste script.
 */
function sincronizarConfiguracoesDoPainel_() {
  const url = URL_ENDPOINT_FUNCIONARIOS.split('/Home/')[0] + '/Home/ConfiguracoesAutomaticas';
  const opcoes = { method: 'get', muteHttpExceptions: true, headers: {} };
  const propriedades = PropertiesService.getScriptProperties();
  const chave = propriedades.getProperty('FUNCIONARIOS_API_KEY') || CHAVE_API;
  if (chave) opcoes.headers['X-Api-Key'] = chave;
  const resposta = UrlFetchApp.fetch(url, opcoes);
  if (resposta.getResponseCode() < 200 || resposta.getResponseCode() >= 300) throw new Error('Painel indisponível para sincronização: HTTP ' + resposta.getResponseCode());
  const bruto = JSON.parse(resposta.getContentText());
  const lista = Array.isArray(bruto) ? bruto : (bruto.configuracoes || []);
  const store = {
    Exclusoes: Array.isArray(bruto.excluidos) ? bruto.excluidos : [],
    Configuracoes: lista.map(function (x) { return { Id: x.id, RegraId: x.regraId || '', Nome: x.nome || '', Ativo: x.ativo !== false, ModoGatilho: x.modoGatilho || 'Horario', Periodicidade: x.periodicidade || 'Diariamente', Horarios: x.horarios || [], DiaSemana: x.diaSemana || '', DiaMes: x.diaMes || 1, MesAno: x.mesAno || 1, FonteId: x.fonteId || 'fonte-planilha-principal', AbaFonte: x.abaFonte || x.aba || '', Categoria: x.categoria || '', Subcategoria: x.subcategoria || '', PrazoHoras: x.prazoHoras || 24, Titulo: x.titulo || '', Descricao: x.descricao || '', ColunaNumeroChamado: x.colunaChamado || '', ColunaNumeroChamadoFonteId: x.colunaChamadoFonteId || x.fonteId || '', ColunaNumeroChamadoAba: x.colunaChamadoAba || x.abaFonte || x.aba || '' }; }),
    Regras: lista.filter(function (x) { return x.regraId; }).map(function (x) { return { Id: x.regraId, Nome: x.nome || '', FonteId: x.fonteId || 'fonte-planilha-principal', AbaFonte: x.abaFonte || x.aba || '', Condicoes: (x.condicoes || []).map(function (c) { return { coluna: c.coluna || '', operador: c.operador || 'Igual a', tipoValor: c.tipoValor || 'Texto', valor: c.valor || '', periodoQuantidade: c.periodoQuantidade || 1, periodoUnidade: c.periodoUnidade || 'meses', modoData: c.modoData || 'Hoje' }; }), Combinacao: x.combinacao || 'E', Ativa: true }; }),
    Fontes: [{ Id: 'fonte-planilha-principal', Nome: bruto.nome || 'Planilha integrada com o Fluig', Tipo: 'Google Sheets', Endereco: bruto.endereco || '', Abas: bruto.abas || [], Ativa: true }], Historico: []
  };
  // Leitura do painel não grava na planilha. A célula só é atualizada pelo
  // doPost quando o painel publica explicitamente uma alteração.
  return store;
}

function buscarConfiguracoesAutomaticas_() {
  const url = URL_ENDPOINT_FUNCIONARIOS.split('/Home/')[0] + '/Home/ConfiguracoesAutomaticas';
  const opcoes = { method: 'get', muteHttpExceptions: true, headers: {} };
  const propriedades = PropertiesService.getScriptProperties();
  const chave = propriedades.getProperty('FUNCIONARIOS_API_KEY') || (CHAVE_API && CHAVE_API !== 'COLOQUE_AQUI_A_MESMA_CHAVE_DE_FUNCIONARIOS_API_KEY' ? CHAVE_API : '');
  if (chave) opcoes.headers['X-Api-Key'] = chave;
  try {
    const resposta = UrlFetchApp.fetch(url, opcoes);
    if (resposta.getResponseCode() >= 200 && resposta.getResponseCode() < 300) {
      const lista = JSON.parse(resposta.getContentText());
      const resultado = Array.isArray(lista) ? lista : (Array.isArray(lista.configuracoes) ? lista.configuracoes : []);
      const exclusoesPainel = Array.isArray(lista.excluidos) ? lista.excluidos : [];
      return resultado;
    }
    throw new Error('Configuração do painel indisponível. HTTP ' + resposta.getResponseCode() + ': ' + resposta.getContentText().slice(0, 300));
  } catch (erro) {
    const backup = lerBackupConfiguracoes_();
    if (backup.length) { console.log('Painel indisponível; usando backup da aba oculta de configurações.'); return backup; }
    throw new Error('Configuração do painel indisponível: ' + erro);
  }
}

function salvarBackupStore_(store) { try { const aba = garantirAbaConfiguracao_(); aba.getRange(2, 1, Math.max(aba.getMaxRows() - 1, 1), 1).clearContent(); aba.getRange(2, 1).setValue(JSON.stringify(store || {})); aba.hideSheet(); } catch (e) { console.log('Não foi possível salvar backup local: ' + e); } }

function lerBackupStore_() {
  try { const aba = garantirAbaConfiguracao_(); const bruto = String(aba.getRange(2, 1).getValue() || '').trim(); const store = bruto ? JSON.parse(bruto) : {}; return store && typeof store === 'object' ? store : {}; } catch (e) { return {}; }
}

function lerBackupConfiguracoes_() {
  try {
    const aba = garantirAbaConfiguracao_();
    const bruto = String(aba.getRange(2, 1).getValue() || '').trim();
    const lista = bruto ? JSON.parse(bruto) : [];
    return Array.isArray(lista) ? lista : (lista.Configuracoes || []);
  } catch (e) { return []; }
}

function obterIndiceColuna_(sheet, referencia) {
  const valor = String(referencia || '').trim();
  const quantidadeColunas = Math.max(TOTAL_COLUNAS, sheet.getLastColumn(), sheet.getMaxColumns());
  const cabecalhos = sheet.getRange(LINHA_TITULOS, 1, 1, quantidadeColunas).getValues()[0];
  const posicao = cabecalhos.findIndex(function (cabecalho) { return String(cabecalho || '').trim().toUpperCase() === valor.toUpperCase(); });
  if (posicao >= 0) return posicao + 1;
  if (/^[A-Za-z]+$/.test(valor)) {
    let indice = 0;
    for (let i = 0; i < valor.length; i++) indice = indice * 26 + valor.toUpperCase().charCodeAt(i) - 64;
    if (indice > 0 && indice <= sheet.getMaxColumns()) return indice;
  }
  throw new Error('A coluna "' + valor + '" não existe na aba "' + sheet.getName() + '". Informe a letra (A, B, C...) ou o cabeçalho exato.');
}

/**
 * MOTOR GENÉRICO DE AUTOMAÇÕES
 * ----------------------------------------------------------------------
 * Roda QUALQUER automação criada na Central de Chamados do painel — não
 * apenas o desligamento. Cada "config" já vem resolvida do painel com:
 *   { id, nome, aba, condicoes:[{coluna,operador,valor}], colunaChamado,
 *     titulo, descricao, categoria, subcategoria, prazoHoras }
 * Basta criar/editar a regra e o agendamento no painel — nada aqui precisa
 * mudar para uma nova automação passar a funcionar.
 */
function processarAutomacoesGenericas_(ss, configuracoes) { notificarProgramaAutomacao_({tipo:'sincronizacao',ocorridoEm:new Date().toISOString()}); return true; }

/**
 * Avalia uma única condição de uma regra contra o valor de uma célula.
 * Os operadores são exatamente os mesmos oferecidos no formulário de regras
 * do painel — se um novo operador for adicionado lá, adicione aqui também.
 */
function obterDataReferencia_(modoData, valorEsperado) {
  const hoje = zerarHorario_(new Date());
  const modo = String(modoData || 'Hoje').toLowerCase();
  if (modo === 'hoje') return hoje;
  if (modo === 'ontem') { hoje.setDate(hoje.getDate() - 1); return hoje; }
  const match = modo.match(/(\d+)\s*(dia|dias|mes|meses|ano|anos)/);
  if (match) {
    const quantidade = Number(match[1]);
    if (match[2].indexOf('dia') === 0) hoje.setDate(hoje.getDate() - quantidade);
    else if (match[2].indexOf('ano') === 0) hoje.setFullYear(hoje.getFullYear() - quantidade);
    else hoje.setMonth(hoje.getMonth() - quantidade);
    return hoje;
  }
  return converterDataPlanilha_(valorEsperado);
}

function avaliarCondicao_(valorCelula, operador, valorEsperado, tipoValor, periodoQuantidade, periodoUnidade, modoData) {
  const texto = String(valorCelula == null ? '' : valorCelula).trim();
  const esperado = String(valorEsperado == null ? '' : valorEsperado).trim();
  tipoValor = tipoValor || 'Texto';
  if (operador === 'Nulo' || operador === 'Está vazio') return texto === '';
  if (tipoValor === 'Período') {
    const d = converterDataPlanilha_(valorCelula); if (!d) return false;
    const qtd = Math.max(1, Number(periodoQuantidade || 1));
    const unidade = String(periodoUnidade || 'meses').toLowerCase();
    const limite = zerarHorario_(new Date());
    if (unidade.indexOf('dia') === 0) limite.setDate(limite.getDate() - qtd);
    else if (unidade.indexOf('ano') === 0) limite.setFullYear(limite.getFullYear() - qtd);
    else limite.setMonth(limite.getMonth() - qtd);
    if (operador === 'Menor que') return d < limite;
    if (operador === 'Diferente de') return d.getTime() !== limite.getTime();
    return d.getTime() === limite.getTime();
  }
  if (tipoValor === 'Data') {
    const d = converterDataPlanilha_(valorCelula); if (!d) return false;
    const alvo = obterDataReferencia_(modoData, esperado); if (!alvo) return false;
    d.setHours(0,0,0,0); alvo.setHours(0,0,0,0);
    if (operador === 'Menor que') return d < alvo;
    if (operador === 'Diferente de') return d.getTime() !== alvo.getTime();
    return d.getTime() === alvo.getTime();
  }
  if (tipoValor === 'Número') {
    const n=Number(texto.replace(',', '.')), alvo=Number(esperado.replace(',', '.')); if(isNaN(n)||isNaN(alvo)) return false;
    if(operador==='Menor que') return n<alvo; if(operador==='Diferente de') return n!==alvo; return n===alvo;
  }
  if (operador === 'Menor que') return texto.localeCompare(esperado, undefined, { sensitivity: 'base' }) < 0;
  if (operador === 'Diferente de') return texto.toUpperCase() !== esperado.toUpperCase();
  return texto.toUpperCase() === esperado.toUpperCase();
}

function abrirChamadoAutomatico_(config, registro, sheet, alerta) { throw new Error('A abertura de chamados fica no programa C#.'); }

/**
 * Roda periodicamente (a cada minuto, ver garantirGatilhoExecutorAutomatico_)
 * e dispara as automações do tipo "Horário programado" cujo horário
 * configurado no painel bate com o horário atual — sem depender do
 * agendamento da sincronização diária de funcionários.
 */
function executarAutomacoesAgendadas_() { notificarGatilhoHorario_(); }

function garantirGatilhoExecutorAutomatico_() { /* compatibilidade; executor no C# */ }

/**
 * Modo de simulação (somente leitura): mostra, para cada automação ativa,
 * quantas linhas seriam elegíveis agora e não abre nenhum chamado. Use
 * antes de ativar uma automação nova ou depois de mudar uma regra.
 */
/**
 * Roda manualmente (pelo menu) TODAS as automações ativas agora, sem
 * esperar o horário programado nem uma atualização de dados. Útil para
 * testar uma regra recém-criada no painel.
 */
function executarTodasAutomacoesAgora() { notificarProgramaAutomacao_({tipo:'execucao_manual',ocorridoEm:new Date().toISOString()}); mostrarMensagem_('Solicitação enviada ao programa.'); }

function diagnosticarAutomacoes_() {
  const ss = SpreadsheetApp.getActive();
  const configuracoes = buscarConfiguracoesAutomaticas_();
  const linhas = [];
  configuracoes.forEach(function (config) {
    const sheet = ss.getSheetByName(config.aba || NOME_ABA);
    if (!sheet) { linhas.push(config.nome + ': ABA "' + config.aba + '" NÃO ENCONTRADA'); return; }
    if (!config.ativo) { linhas.push(config.nome + ': inativa, ignorada.'); return; }
    try {
      const colunaChamado = config.colunaChamado ? obterIndiceColuna_(sheet, config.colunaChamado) : null;
      const primeiraLinhaDados = nomeAba === NOME_ABA ? PRIMEIRA_LINHA_DADOS : 2;
      const ultimaLinha = sheet.getLastRow();
      if (ultimaLinha < primeiraLinhaDados) { linhas.push(config.nome + ': sem linhas de dados.'); return; }
      const ultimaColuna = Math.max(TOTAL_COLUNAS, sheet.getLastColumn());
      const dados = sheet.getRange(primeiraLinhaDados, 1, ultimaLinha - primeiraLinhaDados + 1, ultimaColuna).getValues();
      const condicoesResolvidas = config.condicoes.map(function (c) { return { indice: obterIndiceColuna_(sheet, c.coluna) - 1, operador: c.operador, valor: c.valor, tipoValor: c.tipoValor, periodoQuantidade: c.periodoQuantidade, periodoUnidade: c.periodoUnidade, modoData: c.modoData }; });
      let elegiveis = 0, jaTemChamado = 0;
      dados.forEach(function (linha) {
        if (linha[COL_ORIGEM - 1] === ORIGEM_MANUAL) return;
        // Ter um número na coluna de retorno não impede a avaliação. Isso só
        // acontece se a própria regra possuir a condição "Está vazio".
        if ((String(config.combinacao || 'E').toUpperCase() === 'OU' ? condicoesResolvidas.some(function (c) { return avaliarCondicao_(linha[c.indice], c.operador, c.valor, c.tipoValor, c.periodoQuantidade, c.periodoUnidade, c.modoData); }) : condicoesResolvidas.every(function (c) { return avaliarCondicao_(linha[c.indice], c.operador, c.valor, c.tipoValor, c.periodoQuantidade, c.periodoUnidade, c.modoData); }))) elegiveis++;
      });
      linhas.push(config.nome + ' (aba ' + nomeAba + '): ' + elegiveis + ' elegível(is) agora. A coluna de retorno só bloqueia se houver uma condição explícita "Está vazio".');
    } catch (erro) { linhas.push(config.nome + ': ERRO — ' + erro); }
  });
  const relatorio = 'Diagnóstico de automações (nenhum chamado foi aberto):\n\n' + linhas.join('\n');
  console.log(relatorio);
  mostrarMensagem_(relatorio);
}

function substituirVariaveisLinha_(modelo, registro, sheet) {
  const linha = sheet.getRange(registro.numeroLinha, 1, 1, Math.max(TOTAL_COLUNAS, sheet.getLastColumn())).getValues()[0];
  const cabecalhos = sheet.getRange(LINHA_TITULOS, 1, 1, linha.length).getValues()[0];
  return String(modelo || '').replace(/\$([A-Za-z_][A-Za-z0-9_]*)/g, function (_, referencia) {
    try {
      const posCabecalho = cabecalhos.findIndex(function (cabecalho) { return String(cabecalho || '').trim().toUpperCase() === referencia.toUpperCase(); });
      const indice = posCabecalho >= 0 ? posCabecalho + 1 : obterIndiceColuna_(sheet, referencia);
      const valor = linha[indice - 1];
      return valor instanceof Date ? formatarData_(valor) : String(valor == null ? '' : valor);
    } catch (erro) { return '$' + referencia; }
  });
}

function extrairNumeroChamado_(corpo) {
  try {
    const json = JSON.parse(corpo);
    const candidatos = [json.processInstanceId, json.processInstanceID, json.requestNumber, json.numeroChamado, json.codigo, json.documentId, json.id, json.data && json.data.processInstanceId, json.data && json.data.requestNumber];
    for (let i = 0; i < candidatos.length; i++) {
      if (candidatos[i] !== undefined && candidatos[i] !== null && String(candidatos[i]).trim()) return String(candidatos[i]);
    }
  } catch (erro) {
    // Algumas versões do Fluig podem retornar texto em vez de JSON.
  }
  const numero = String(corpo).match(/\b\d{4,}\b/);
  if (numero) return numero[0];
  throw new Error('Chamado enviado, mas não foi possível identificar o número retornado pelo Fluig: ' + String(corpo).slice(0, 500));
}

function converterDataPlanilha_(valor) {
  if (Object.prototype.toString.call(valor) === '[object Date]' && !isNaN(valor.getTime())) return zerarHorario_(valor);
  const texto = String(valor || '').trim();
  let match = texto.match(/^(\d{2})\/(\d{2})\/(\d{4})$/);
  if (match) return new Date(Number(match[3]), Number(match[2]) - 1, Number(match[1]));
  match = texto.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (match) return new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return null;
}

function zerarHorario_(data) {
  return new Date(data.getFullYear(), data.getMonth(), data.getDate());
}

function formatarData_(data) {
  return Utilities.formatDate(data, 'America/Sao_Paulo', 'dd/MM/yyyy');
}

function buscarFuncionariosNoEndpoint_() {
  const opcoes = {
    method: 'get',
    muteHttpExceptions: true,
    headers: {}
  };
  if (CHAVE_API && CHAVE_API !== 'COLOQUE_AQUI_A_MESMA_CHAVE_DE_FUNCIONARIOS_API_KEY') {
    opcoes.headers['X-Api-Key'] = CHAVE_API;
  }

  const response = UrlFetchApp.fetch(URL_ENDPOINT_FUNCIONARIOS, opcoes);
  const code = response.getResponseCode();
  if (code < 200 || code >= 300) {
    throw new Error('Erro ao consultar o endpoint. HTTP ' + code + ': ' + response.getContentText().slice(0, 300));
  }

  const registros = JSON.parse(response.getContentText());
  if (!Array.isArray(registros)) {
    throw new Error('Resposta inesperada do endpoint: esperava uma lista.');
  }
  return registros;
}

function converterData_(valor) {
  if (!valor) return '';
  const match = String(valor).match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (match) return new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return '';
}

/**
 * Ajusta a largura das colunas visíveis ao conteúdo atual, com uma exceção:
 * a coluna CHAMADOS_ALTERACAO não cresce — fica com largura fixa e quebra
 * automática de texto (pra não esticar a planilha inteira quando alguém
 * anota várias alterações ali).
 */
function ajustarLargurasColunas_(sheet) {
  const larguraFixaAlteracao = 200;

  // A linha 1 (orientações) tem uma célula mesclada — qualquer coluna com
  // célula mesclada faz o autoResizeColumns calcular uma largura absurda,
  // mesmo sem texto. Solução: desfaz a mesclagem e limpa o texto antes de
  // ajustar as larguras, e remescla + devolve o texto depois.
  const celulaOrientacoes = sheet.getRange(LINHA_ORIENTACOES, 1); // só a célula A
  const rangeOrientacoes = sheet.getRange(LINHA_ORIENTACOES, 1, 1, TOTAL_COLUNAS - 1); // A1:J1 (mesclado)
  const textoOrientacoes = celulaOrientacoes.getValue();

  rangeOrientacoes.breakApart();
  rangeOrientacoes.clearContent();

  sheet.autoResizeColumns(1, COL_CHAMADO_ALTERACAO - 1);
  sheet.autoResizeColumns(COL_CHAMADO_DESLIGAMENTO, 1);

  celulaOrientacoes.setValue(textoOrientacoes);
  rangeOrientacoes.merge();
  rangeOrientacoes.setWrap(true);

  // Coluna de chamados de alteração: largura fixa + quebra automática
  sheet.setColumnWidth(COL_CHAMADO_ALTERACAO, larguraFixaAlteracao);
  const ultimaLinha = Math.max(sheet.getLastRow(), PRIMEIRA_LINHA_DADOS);
  sheet.getRange(LINHA_TITULOS, COL_CHAMADO_ALTERACAO, ultimaLinha - LINHA_TITULOS + 1, 1)
    .setWrap(true);
}

/**
 * Cria (ou recria) o filtro do Google Sheets sobre a área de cabeçalho +
 * dados, pra você poder filtrar/ordenar pelas setinhas no topo de cada
 * coluna. Precisa ser recriado depois de inserir/apagar linhas porque o
 * Apps Script às vezes perde a referência do filtro antigo.
 */
function aplicarFiltro_(sheet) {
  const filtroAtual = sheet.getFilter();
  if (filtroAtual) filtroAtual.remove();

  const ultimaLinha = Math.max(sheet.getLastRow(), PRIMEIRA_LINHA_DADOS);
  sheet.getRange(LINHA_TITULOS, 1, ultimaLinha - LINHA_TITULOS + 1, TOTAL_COLUNAS).createFilter();
}

// ============================================================================
// REGISTRO MANUAL
// ============================================================================

function abrirFormularioManual() {
  const html = HtmlService.createHtmlOutputFromFile('Formulario')
    .setWidth(420)
    .setHeight(560);
  SpreadsheetApp.getUi().showModalDialog(html, 'Adicionar registro manual');
}

/**
 * Chamada pelo formulário (Formulario.html) via google.script.run.
 * Insere a nova linha manual logo após o cabeçalho (linha 3), em amarelo.
 */
function adicionarRegistroManual(dados) {
  const ss = SpreadsheetApp.getActive();
  const sheet = ss.getSheetByName(NOME_ABA);
  if (!sheet) throw new Error('A aba "' + NOME_ABA + '" não existe. Execute configurarPlanilha() primeiro.');

  sheet.insertRowBefore(PRIMEIRA_LINHA_DADOS);
  const linha = PRIMEIRA_LINHA_DADOS;

  const valores = [[
    dados.chapa || '',
    dados.funcionario || '',
    dados.cpf || '',
    dados.funcao || '',
    dados.setor || '',
    dados.situacao || '',
    converterData_(dados.dataAdmissao),
    converterData_(dados.dataDemissao),
    dados.chamadoCriacao || '',
    dados.chamadoAlteracao || '',
    dados.chamadoDesligamento || '',
    ORIGEM_MANUAL,
  ]];

  const destino = sheet.getRange(linha, 1, 1, TOTAL_COLUNAS);
  destino.setValues(valores);
  destino.setBackground(COR_MANUAL);
  sheet.getRange(linha, COL_CPF, 1, 1).setNumberFormat('@');
  sheet.getRange(linha, COL_CHAPA, 1, 1).setNumberFormat('@');
  sheet.getRange(linha, COL_DATAADMISSAO, 1, 2).setNumberFormat('dd/mm/yyyy');

  aplicarFiltro_(sheet);
  ajustarLargurasColunas_(sheet);

  notificarProgramaAutomacao_({tipo:'edicao',aba:NOME_ABA,linhaInicial:linha,quantidadeLinhas:1,colunaInicial:1,quantidadeColunas:TOTAL_COLUNAS,ocorridoEm:new Date().toISOString()});
  return 'ok';
}

// ============================================================================
// INVENTÁRIO GUIADO DE EQUIPAMENTOS
// ============================================================================
const NOME_ABA_INVENTARIO = 'Equipamentos';
const NOME_ABA_INVENTARIO_TEMP = 'INVENTÁRIO EM ANDAMENTO';
const NOME_ABA_AVISO_INVENTARIO = 'AVISO INVENTÁRIO';
const CABECALHOS_INVENTARIO = ['Tipo', 'Modelo', 'Local', 'Alugado', 'Patrimônio', 'Status', 'Último preventivo', 'próximo preventivo', 'fluig ult. preventivo'];
const DESCRICAO_PROTECAO_INVENTARIO = 'PROTECAO_INVENTARIO_GUIADO';

function localizarCabecalhoInventario_(sheet) {
  const limite = Math.min(Math.max(sheet.getLastRow(), 1), 12);
  const valores = sheet.getRange(1, 1, limite, Math.min(Math.max(sheet.getLastColumn(), 9), 20)).getDisplayValues();
  for (let i = 0; i < valores.length; i++) {
    const linha = valores[i].map(function (x) { return String(x || '').trim().toLowerCase(); });
    if (linha.indexOf('patrimônio') >= 0 || linha.indexOf('patrimonio') >= 0) return i + 1;
  }
  return 1;
}

function indiceCabecalhoInventario_(cabecalhos, nomes) {
  const normalizar = function (x) { return String(x || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').trim().toLowerCase(); };
  const desejados = nomes.map(normalizar);
  for (let i = 0; i < cabecalhos.length; i++) if (desejados.indexOf(normalizar(cabecalhos[i])) >= 0) return i;
  return -1;
}

function obterOpcoesInventario() {
  const cache = CacheService.getDocumentCache();
  const cacheKey = 'INVENTARIO_OPCOES_V2';
  const guardado = cache.get(cacheKey);
  if (guardado) { try { return JSON.parse(guardado); } catch (e) {} }
  const sheet = SpreadsheetApp.getActive().getSheetByName(NOME_ABA_INVENTARIO);
  if (!sheet) throw new Error('A aba "' + NOME_ABA_INVENTARIO + '" não existe.');
  const headerRow = localizarCabecalhoInventario_(sheet), ncol = Math.max(sheet.getLastColumn(), 9);
  const lastRow = sheet.getLastRow();
  const rows = lastRow >= headerRow ? sheet.getRange(headerRow, 1, lastRow - headerRow + 1, ncol).getDisplayValues() : [];
  const head = rows.shift() || CABECALHOS_INVENTARIO;
  const idx = {tipo: indiceCabecalhoInventario_(head, ['Tipo', 'Tipo de equipamento']), modelo: indiceCabecalhoInventario_(head, ['Modelo']), local: indiceCabecalhoInventario_(head, ['Local']), status: indiceCabecalhoInventario_(head, ['Status', 'Situação', 'Situacao'])};
  const unicos = function (i) { return i < 0 ? [] : Array.from(new Set(rows.map(function (r) { return String(r[i] || '').trim(); }).filter(Boolean))).sort(); };
  const porValidacao = function (i) { if (i < 0) return []; const regras = sheet.getRange(1, i + 1, Math.min(sheet.getMaxRows(), 1000), 1).getDataValidations(), valores = []; regras.forEach(function (linha) { const regra = linha[0]; if (!regra) return; try { const tipo = regra.getCriteriaType(), c = regra.getCriteriaValues() || []; if (tipo === SpreadsheetApp.DataValidationCriteria.VALUE_IN_LIST) (c[0] || []).forEach(function (v) { valores.push(String(v).trim()); }); else if (tipo === SpreadsheetApp.DataValidationCriteria.VALUE_IN_RANGE && c[0]) c[0].getDisplayValues().forEach(function (r) { if (r[0]) valores.push(String(r[0]).trim()); }); } catch (e) {} }); return Array.from(new Set(valores.filter(Boolean))).sort(); };
  const opcoes = function (i) { const lista = porValidacao(i).concat(unicos(i)); return Array.from(new Set(lista.map(function (x) { return String(x || '').trim(); }).filter(Boolean))).sort(); };
  const resultado = { tipos: opcoes(idx.tipo), modelos: opcoes(idx.modelo), locais: opcoes(idx.local), status: opcoes(idx.status), iniciado: true };
  try { cache.put(cacheKey, JSON.stringify(resultado), 120); } catch (e) {}
  return resultado;
}

function obterRegistrosInventarioSessao() {
  const raw = PropertiesService.getDocumentProperties().getProperty('INVENTARIO_SESSAO_REGISTROS') || '[]';
  try {
    const lista = JSON.parse(raw), resultado = [];
    lista.forEach(function (x) { const n=Math.max(1, Number(x.quantidade || 1)); for (let i=0;i<n;i++) resultado.push(x); });
    return resultado;
  } catch (e) { return []; }
}

function adicionarRegistroSessaoInventario_(dados, atualizado, quantidade) {
  const props = PropertiesService.getDocumentProperties();
  let lista = [];
  try { lista = JSON.parse(props.getProperty('INVENTARIO_SESSAO_REGISTROS') || '[]'); } catch (e) { lista = []; }
  const qtd = Math.max(1, Number(quantidade || 1));
  const item = {tipo: String(dados.tipo || ''), modelo: String(dados.modelo || ''), local: String(dados.local || ''), alugado: String(dados.alugado || ''), patrimonio: String(dados.patrimonio || ''), status: String(dados.status || ''), registradoEm: new Date(), atualizado: !!atualizado, quantidade: qtd};
  const chave = normalizarPatrimonioInventario_(item.patrimonio), especial = !chave || /FECHADO$/i.test(chave);
  if (especial) lista.push(item);
  else { const i=lista.findIndex(function (x) { return normalizarPatrimonioInventario_(x.patrimonio).toLowerCase()===chave.toLowerCase(); }); if (i>=0) lista[i]=item; else lista.push(item); }
  props.setProperty('INVENTARIO_SESSAO_REGISTROS', JSON.stringify(lista));
}


function criarBackupInventario_() {
  const ss = SpreadsheetApp.getActive(); const principal = ss.getSheetByName(NOME_ABA_INVENTARIO);
  let backup = ss.getSheetByName('_BACKUP_INVENTARIO'); if (!backup) backup = ss.insertSheet('_BACKUP_INVENTARIO');
  backup.showSheet(); backup.clear();
  const rows = Math.max(principal.getLastRow(), 1), cols = Math.max(principal.getLastColumn(), 1);
  principal.getRange(1, 1, rows, cols).copyTo(backup.getRange(1, 1, rows, cols), SpreadsheetApp.CopyPasteType.PASTE_NORMAL, false);
  backup.hideSheet();
  backup.getProtections(SpreadsheetApp.ProtectionType.SHEET).forEach(function (x) { if (x.getDescription() === 'PROTECAO_BACKUP_INVENTARIO') x.remove(); });
  const prot = backup.protect().setDescription('PROTECAO_BACKUP_INVENTARIO'); prot.setWarningOnly(false); try { prot.removeEditors(prot.getEditors()); } catch (e) {}
}

function prepararAbaAvisoInventario_() {
  const ss = SpreadsheetApp.getActive();
  let aviso = ss.getSheetByName(NOME_ABA_AVISO_INVENTARIO);
  if (!aviso) aviso = ss.insertSheet(NOME_ABA_AVISO_INVENTARIO);
  aviso.showSheet();
  if (aviso.getMaxColumns() < 8) aviso.insertColumnsAfter(aviso.getMaxColumns(), 8 - aviso.getMaxColumns());
  if (aviso.getMaxRows() < 12) aviso.insertRowsAfter(aviso.getMaxRows(), 12 - aviso.getMaxRows());
  aviso.getRange(1, 1, 12, 8).clearContent().clearFormat();
  aviso.getRange(1, 1, 10, 8).merge();
  aviso.getRange(1, 1).setValue('INVENTÁRIO EM ANDAMENTO\n\nNÃO MEXA NA ABA EQUIPAMENTOS NEM NA ABA INVENTÁRIO EM ANDAMENTO.\n\nProcure o responsável pelo inventário antes de mover, instalar, desativar ou alterar qualquer equipamento. Toda ação manual nessas abas será desfeita.');
  aviso.getRange(1, 1, 10, 8).setBackground('#fee2e2').setFontColor('#991b1b').setFontWeight('bold').setFontSize(22).setHorizontalAlignment('center').setVerticalAlignment('middle').setWrap(true);
  aviso.setRowHeights(1, 10, 38); aviso.setColumnWidths(1, 8, 130); aviso.setFrozenRows(10); aviso.setTabColor('#cc0000'); aviso.activate();
  return aviso;
}
function removerAbaAvisoInventario_() {
  const ss = SpreadsheetApp.getActive(), aviso = ss.getSheetByName(NOME_ABA_AVISO_INVENTARIO);
  if (aviso && ss.getSheets().length > 1) ss.deleteSheet(aviso);
}

function mostrarAvisoInventarioNasAbas_() {
  const ss = SpreadsheetApp.getActive(); const principal = ss.getSheetByName(NOME_ABA_INVENTARIO); const temp = ss.getSheetByName(NOME_ABA_INVENTARIO_TEMP);
  const texto = 'INVENTÁRIO EM ANDAMENTO\n\nNÃO ALTERE ESTA ABA.\n\nAntes de mover, instalar, desativar ou alterar o status de um equipamento, avise o responsável pelo inventário. As alterações feitas aqui serão revertidas.';
  if (principal) { principal.getRange(1, 1).setNote(texto); principal.setTabColor('#cc0000'); }
  if (temp) temp.getRange(1, 1).setNote('INVENTÁRIO EM ANDAMENTO\n\nUse somente o formulário do inventário pelo celular. Não edite esta aba manualmente.');
}

function garantirGatilhoBloqueioInventario_() {
  const gatilhos = ScriptApp.getProjectTriggers();
  if (!gatilhos.some(function (t) { return t.getHandlerFunction() === 'onEdit'; })) try { ScriptApp.newTrigger('onEdit').forSpreadsheet(SpreadsheetApp.getActive()).onEdit().create(); } catch (e) { console.log('Não foi possível criar gatilho de bloqueio: ' + e); }
  if (!gatilhos.some(function (t) { return t.getHandlerFunction() === 'onChangeInventario'; })) try { ScriptApp.newTrigger('onChangeInventario').forSpreadsheet(SpreadsheetApp.getActive()).onChange().create(); } catch (e) { console.log('Não foi possível criar gatilho estrutural: ' + e); }
}

function removerBackupInventario_() {
  const ss = SpreadsheetApp.getActive(), backup = ss.getSheetByName('_BACKUP_INVENTARIO');
  if (backup) { backup.getProtections(SpreadsheetApp.ProtectionType.SHEET).forEach(function (x) { if (x.getDescription() === 'PROTECAO_BACKUP_INVENTARIO') x.remove(); }); ss.deleteSheet(backup); }
}

function restaurarEdicaoTempInventario_(range) {
  const sheet = range.getSheet(), lista = obterRegistrosInventarioSessao();
  sheet.getRange(1, 1, 1, 7).merge().setValue('INVENTÁRIO EM ANDAMENTO\n\nNÃO MEXA NESTA ABA NEM NA ABA EQUIPAMENTOS.\nProcure o responsável pelo inventário antes de mover, instalar, desativar ou alterar qualquer equipamento. Toda ação manual nesta aba será desfeita.').setBackground('#fee2e2').setFontColor('#991b1b').setFontWeight('bold').setFontSize(16).setHorizontalAlignment('center').setVerticalAlignment('middle').setWrap(true); sheet.getRange(2, 1, 1, 7).setValues([['Tipo', 'Modelo', 'Local', 'Alugado', 'Patrimônio', 'Status', 'Registrado em']]).setFontWeight('bold').setBackground('#1f4e78').setFontColor('#ffffff');
  const inicio = 3, linhaInicial = range.getRow(), quantidade = range.getNumRows();
  for (let i = 0; i < quantidade; i++) {
    const indice = linhaInicial + i - inicio;
    const item = lista[indice];
    if (item) sheet.getRange(linhaInicial + i, 1, 1, 7).setValues([[item.tipo || '', item.modelo || '', item.local || '', item.alugado || '', normalizarPatrimonioInventario_(item.patrimonio), item.status || '', item.registradoEm || new Date()]]);
    else sheet.getRange(linhaInicial + i, 1, 1, Math.max(sheet.getLastColumn(), 7)).clearContent();
  }
}

function onChangeInventario(e) {
  const props = PropertiesService.getDocumentProperties();
  try {
    if (props.getProperty('INVENTARIO_RESTAURANDO') === '1' || !props.getProperty('INVENTARIO_EM_ANDAMENTO') || !e) return;
    if (['REMOVE_ROW','REMOVE_COLUMN','INSERT_ROW','INSERT_COLUMN'].indexOf(String(e.changeType || '')) < 0) return;
    props.setProperty('INVENTARIO_RESTAURANDO', '1');
    const ss=SpreadsheetApp.getActive(), principal=ss.getSheetByName(NOME_ABA_INVENTARIO);
    if (!principal) return;
    restaurarBackupInventario_();
    principal.insertRowBefore(1);
    principal.getRange(1,1,1,Math.max(principal.getLastColumn(),9)).merge().setValue('INVENTÁRIO EM ANDAMENTO — NÃO ALTERE Tipo, Modelo, Local, Alugado, Patrimônio ou Status. Para registrar preventivo, use as colunas após Status. Avise o responsável antes de mover, instalar ou alterar equipamentos.').setBackground('#fee2e2').setFontColor('#991b1b').setFontWeight('bold').setFontSize(16).setHorizontalAlignment('center').setVerticalAlignment('middle').setWrap(true);
    principal.setRowHeight(1,100); principal.setFrozenRows(localizarCabecalhoInventario_(principal)); principal.hideSheet();
    mostrarMensagem_('A alteração estrutural foi desfeita. Não apague nem insira linhas durante o inventário.');
  } catch (erro) { console.log('Não foi possível restaurar a estrutura do inventário: ' + erro); }
  finally { props.deleteProperty('INVENTARIO_RESTAURANDO'); }
}

function onEdit(e) {
  try {
    if (!PropertiesService.getDocumentProperties().getProperty('INVENTARIO_EM_ANDAMENTO') || !e || !e.range) return;
    const sheet = e.range.getSheet(); const nome = sheet.getName();
    if (nome === NOME_ABA_INVENTARIO_TEMP) { if (e.range.getRow() <= 2) { e.range.getSheet().getRange(1, 1, 1, 7).merge().setValue('INVENTÁRIO EM ANDAMENTO\n\nNÃO MEXA NESTA ABA NEM NA ABA EQUIPAMENTOS.\nProcure o responsável pelo inventário antes de mover, instalar, desativar ou alterar qualquer equipamento. Toda ação manual nesta aba será desfeita.').setBackground('#fee2e2').setFontColor('#991b1b').setFontWeight('bold').setFontSize(16).setHorizontalAlignment('center').setVerticalAlignment('middle').setWrap(true); e.range.getSheet().getRange(2, 1, 1, 7).setValues([['Tipo', 'Modelo', 'Local', 'Alugado', 'Patrimônio', 'Status', 'Registrado em']]).setFontWeight('bold').setBackground('#1f4e78').setFontColor('#ffffff'); } else restaurarEdicaoTempInventario_(e.range); mostrarAvisoInventarioNasAbas_(); mostrarMensagem_('INVENTÁRIO EM ANDAMENTO: esta aba é somente para consulta. Use o formulário e avise o responsável antes de alterar qualquer equipamento.'); return; }
    if (nome !== NOME_ABA_INVENTARIO) return;
    const backup = SpreadsheetApp.getActive().getSheetByName('_BACKUP_INVENTARIO'); if (!backup) { e.range.clearContent(); mostrarAvisoInventarioNasAbas_(); return; }
    const r = e.range;
    // As colunas após F são destinadas a preventivos e chamados; ficam livres durante o inventário.
    if (r.getColumn() > 6) return;
    // A linha 1 é o aviso temporário; a linha editada N corresponde à linha N-1 no backup original.
    const linhaBackup = Math.max(1, r.getRow() - 1);
    const colunaBackup = r.getColumn();
    const rows = Math.min(r.getNumRows(), backup.getMaxRows() - linhaBackup + 1);
    const cols = Math.min(r.getNumColumns(), backup.getMaxColumns() - colunaBackup + 1);
    if (rows > 0 && cols > 0) r.setValues(backup.getRange(linhaBackup, colunaBackup, rows, cols).getValues());
    mostrarAvisoInventarioNasAbas_(); mostrarMensagem_('INVENTÁRIO EM ANDAMENTO: sua alteração foi revertida. Procure o responsável antes de alterar a planilha.');
  } catch (erro) { console.log('Não foi possível reverter edição durante inventário: ' + erro); }
}

function limparEstadoOrfaoInventario_() {
  const props = PropertiesService.getDocumentProperties();
  if (!props.getProperty('INVENTARIO_EM_ANDAMENTO')) return false;
  const ss = SpreadsheetApp.getActive();
  const temp = ss.getSheetByName(NOME_ABA_INVENTARIO_TEMP);
  if (temp) return false;
  const principal = ss.getSheetByName(NOME_ABA_INVENTARIO);
  if (principal) principal.getProtections(SpreadsheetApp.ProtectionType.SHEET).forEach(function (x) { x.remove(); });
  props.deleteProperty('INVENTARIO_EM_ANDAMENTO');
  props.deleteProperty('INVENTARIO_TOKEN');
  props.deleteProperty('INVENTARIO_SESSAO_REGISTROS');
  removerBackupInventario_();
  removerAbaAvisoInventario_();
  try { CacheService.getDocumentCache().remove('INVENTARIO_OPCOES_V2'); } catch (e) {}
  return true;
}

function ativarAbaNeutraDuranteInventario_(ss) {
  const bloqueadas = [NOME_ABA_INVENTARIO, NOME_ABA_INVENTARIO_TEMP];
  const candidata = ss.getSheets().find(function (sh) { return bloqueadas.indexOf(sh.getName()) < 0 && !sh.isSheetHidden(); });
  if (candidata) candidata.activate();
}

function iniciarInventarioEquipamentos() {
  const ui = SpreadsheetApp.getUi();
  const estadoOrfao = limparEstadoOrfaoInventario_();
  if (estadoOrfao) mostrarMensagem_('O inventário anterior não tinha mais a aba temporária. O estado foi encerrado e um novo inventário pode ser iniciado.');
  if (PropertiesService.getDocumentProperties().getProperty('INVENTARIO_EM_ANDAMENTO')) {
    const url = obterUrlWebAppInventario_() + '?acao=inventario';
    mostrarMensagem_('Já existe um inventário em andamento.\n\nUse o link já gerado:\n' + url + '\n\nFinalize o inventário atual antes de iniciar outro.');
    return;
  }
  const resposta = ui.alert('Iniciar inventário', 'A aba Equipamentos será protegida e os lançamentos serão feitos em uma aba temporária. Itens já existentes serão atualizados pelo patrimônio, preservando as colunas de manutenção. Deseja continuar?', ui.ButtonSet.YES_NO);
  if (resposta !== ui.Button.YES) return;
  const ss = SpreadsheetApp.getActive();
  const principal = ss.getSheetByName(NOME_ABA_INVENTARIO);
  if (!principal) throw new Error('A aba "' + NOME_ABA_INVENTARIO + '" não existe.');
  removerAbaAvisoInventario_();
  let temp = ss.getSheetByName(NOME_ABA_INVENTARIO_TEMP);
  if (!temp) temp = ss.insertSheet(NOME_ABA_INVENTARIO_TEMP);
  temp.showSheet();
  if (principal.getFilter()) principal.getFilter().remove();
  if (temp.getFilter()) temp.getFilter().remove();
  criarBackupInventario_();
  principal.insertRowBefore(1);
  principal.getRange(1, 1, 1, Math.max(principal.getLastColumn(), 9)).merge().setValue('INVENTÁRIO EM ANDAMENTO — NÃO ALTERE Tipo, Modelo, Local, Alugado, Patrimônio ou Status. Para registrar preventivo, use as colunas após Status. Avise o responsável antes de mover, instalar ou alterar equipamentos.').setBackground('#fee2e2').setFontColor('#991b1b').setFontWeight('bold').setFontSize(16).setHorizontalAlignment('center').setVerticalAlignment('middle').setWrap(true);
  principal.setRowHeight(1, 100);
  principal.setFrozenRows(localizarCabecalhoInventario_(principal));
  temp.clear();
  const filtroTemp = temp.getFilter();
  if (filtroTemp) filtroTemp.remove();
  temp.getRange(1, 1, 1, 7).merge().setValue('INVENTÁRIO EM ANDAMENTO\n\nNÃO MEXA NESTA ABA NEM NA ABA EQUIPAMENTOS.\nProcure o responsável pelo inventário antes de mover, instalar, desativar ou alterar qualquer equipamento. Toda ação manual nesta aba será desfeita.').setBackground('#fee2e2').setFontColor('#991b1b').setFontWeight('bold').setFontSize(16).setHorizontalAlignment('center').setVerticalAlignment('middle').setWrap(true);
  temp.getRange(2, 1, 1, 7).setValues([['Tipo', 'Modelo', 'Local', 'Alugado', 'Patrimônio', 'Status', 'Registrado em']]).setFontWeight('bold').setBackground('#1f4e78').setFontColor('#ffffff');
  temp.setTabColor('#cc0000');
  principal.setTabColor('#cc0000');
  principal.getRange(1, 1).setNote('INVENTÁRIO EM ANDAMENTO: a aba está protegida. Use o formulário e finalize o inventário pelo menu Ação Manual.');
  temp.setRowHeight(1, 100);
  temp.setFrozenRows(2);
  temp.getRange('G:G').setNumberFormat('dd/mm/yyyy hh:mm');
  [120, 170, 170, 80, 120, 110, 125].forEach(function (w, i) { temp.setColumnWidth(i + 1, w); });
  temp.getRange(2, 1, 1, 7).createFilter();
  principal.getProtections(SpreadsheetApp.ProtectionType.SHEET).forEach(function (x) { if (x.getDescription() === DESCRICAO_PROTECAO_INVENTARIO) x.remove(); });
  const protection = principal.protect().setDescription(DESCRICAO_PROTECAO_INVENTARIO);
  // O Sheets mostra a confirmação nativa antes de qualquer edição manual.
  // O responsável ainda consegue prosseguir conscientemente; o onEdit do projeto
  // desfaz a alteração e orienta a usar o formulário do inventário.
  protection.setWarningOnly(false);
  try { protection.removeEditors(protection.getEditors()); } catch (e) {}
  // O aviso nativo vale somente para as colunas cadastrais A:F.
  if (principal.getMaxColumns() > 6) protection.setUnprotectedRanges([principal.getRange(2, 7, Math.max(principal.getMaxRows() - 1, 1), principal.getMaxColumns() - 6)]);
  temp.getProtections(SpreadsheetApp.ProtectionType.SHEET).forEach(function (x) { if (x.getDescription() === 'PROTECAO_INVENTARIO_TEMP') x.remove(); });
  const protectionTemp = temp.protect().setDescription('PROTECAO_INVENTARIO_TEMP');
  protectionTemp.setWarningOnly(false);
  try { protectionTemp.removeEditors(protectionTemp.getEditors()); } catch (e) {}
  const token = Utilities.getUuid().replace(/-/g, '');
  PropertiesService.getDocumentProperties().setProperty('INVENTARIO_TOKEN', token);
  PropertiesService.getDocumentProperties().setProperty('INVENTARIO_EM_ANDAMENTO', '1');
  PropertiesService.getDocumentProperties().setProperty('INVENTARIO_SESSAO_REGISTROS', '[]');
  garantirGatilhoBloqueioInventario_();
  mostrarAvisoInventarioNasAbas_();
  const link = obterUrlWebAppInventario_() + '?acao=inventario';
  temp.getRange(1, 1).setNote('Link do inventário para o celular: ' + link);
  temp.hideSheet();
  principal.hideSheet();
  ativarAbaNeutraDuranteInventario_(ss);
  mostrarMensagem_('Inventário iniciado. Compartilhe este link com o responsável pelo inventário:\n\n' + link + '\n\nA aba Equipamentos está protegida.\n\nSe a implantação oficial mudar, atualize a URL no código e publique uma nova versão antes de iniciar outro inventário.');
}


function adicionarOpcaoValidacaoInventario_(sheet, coluna, valor) {
  const texto = String(valor || '').trim(); if (!texto) return;
  const ultima = Math.max(sheet.getMaxRows(), 1000);
  const range = sheet.getRange(1, coluna + 1, ultima, 1);
  let existente = []; const validacoes = range.getDataValidations();
  validacoes.forEach(function (linha) { const regra = linha[0]; if (!regra) return; try { const tipo = regra.getCriteriaType(); const c = regra.getCriteriaValues() || []; if (tipo === SpreadsheetApp.DataValidationCriteria.VALUE_IN_LIST) existente = existente.concat(c[0] || []); else if (tipo === SpreadsheetApp.DataValidationCriteria.VALUE_IN_RANGE && c[0]) existente = existente.concat(c[0].getDisplayValues().map(function (r) { return r[0]; })); } catch (e) {} });
  existente = Array.from(new Set(existente.map(function (x) { return String(x || '').trim(); }).filter(Boolean).concat([texto]))).sort();
  const regraNova = SpreadsheetApp.newDataValidation().requireValueInList(existente, true).setAllowInvalid(false).build();
  sheet.getRange(2, coluna + 1, Math.max(sheet.getMaxRows() - 1, 1), 1).setDataValidation(regraNova);
}

function normalizarPatrimonioInventario_(valor) { return String(valor || '').toUpperCase().replace(/\s+/g, ''); }

function atualizarValidacoesFinaisInventario_(sheet, cabecalhos, linhas, headerRow) {
  const indices = {tipo: indiceCabecalhoInventario_(cabecalhos, ['Tipo', 'Tipo de equipamento']), modelo: indiceCabecalhoInventario_(cabecalhos, ['Modelo']), local: indiceCabecalhoInventario_(cabecalhos, ['Local', 'Setor']), status: indiceCabecalhoInventario_(cabecalhos, ['Status', 'Situação', 'Situacao'])};
  const inicio = Math.max((headerRow || 1) + 1, 2);
  [['tipo', indices.tipo], ['modelo', indices.modelo], ['local', indices.local], ['status', indices.status]].forEach(function (item) {
    const col = item[1]; if (col < 0) return;
    const valores = [];
    try {
      const regraAtual = item[0] === 'status' ? sheet.getRange(inicio, col + 1).getDataValidation() : null;
      if (regraAtual) { const tipo = regraAtual.getCriteriaType(), c = regraAtual.getCriteriaValues() || []; if (tipo === SpreadsheetApp.DataValidationCriteria.VALUE_IN_LIST) (c[0] || []).forEach(function (v) { valores.push(String(v).trim()); }); else if (tipo === SpreadsheetApp.DataValidationCriteria.VALUE_IN_RANGE && c[0]) c[0].getDisplayValues().forEach(function (r) { if (r[0]) valores.push(String(r[0]).trim()); }); }
    } catch (e) {}
    const usadosColuna = linhas.map(function (r) { return String(r[col] || '').trim(); }).filter(Boolean);
    if (item[0] === 'status' && usadosColuna.every(function (v) { return valores.some(function (x) { return x.toLowerCase() === v.toLowerCase(); }); })) return;
    linhas.forEach(function (r) { if (r[col]) valores.push(String(r[col]).trim()); });
    const lista = Array.from(new Set(valores.filter(Boolean))).sort(); if (!lista.length) return;
    const regraNova = SpreadsheetApp.newDataValidation().requireValueInList(lista, true).setAllowInvalid(false).build();
    sheet.getRange(inicio, col + 1, Math.max(sheet.getMaxRows() - inicio + 1, 1), 1).setDataValidation(regraNova);
  });
}

function registrarItemInventario(dados) {
  dados = dados || {};
  dados.patrimonio = normalizarPatrimonioInventario_(dados.patrimonio);
  if (!PropertiesService.getDocumentProperties().getProperty('INVENTARIO_EM_ANDAMENTO')) throw new Error('O inventário não está em andamento.');
  const obrigatorios = ['tipo', 'modelo', 'local', 'alugado', 'patrimonio', 'status'];
  obrigatorios.forEach(function (x) { if (!String(dados[x] || '').trim()) throw new Error('Preencha todos os campos obrigatórios.'); });
  const status = String(dados.status || '').trim().toUpperCase();
  const patrimonio = normalizarPatrimonioInventario_(dados.patrimonio);
  const semPatrimonio = patrimonio === 'SEMPATRIMONIO' || patrimonio === 'SEM PATRIMONIO';
  const backupLacrado = status === 'BACKUP' && /^[A-Z]{2,}FECHADO$/.test(patrimonio);
  if (String(dados.alugado).toLowerCase() === 'sim' && !patrimonio) throw new Error('Para equipamento alugado, informe a sigla do fornecedor antes do número. EX.: PS123, MT123, MQ123, SEMPATRIMONIO ou SIGLA+FECHADO quando for Backup lacrado.');
  if (String(dados.alugado).toLowerCase() === 'sim' && !/^[A-Z]{2,}[0-9]+$/.test(patrimonio) && !backupLacrado && !semPatrimonio) throw new Error('Alugado: informe a sigla do fornecedor antes do número. EX.: PS123, MT123, MQ123. Também é permitido SEMPATRIMONIO. Backup lacrado pode usar SIGLA+FECHADO.');
  const especial = semPatrimonio || backupLacrado;
  let quantidade = Number(dados.quantidade || 1);
  if (!especial) quantidade = 1;
  if (!Number.isInteger(quantidade) || quantidade < 1 || quantidade > 500) throw new Error('A quantidade deve ser um número inteiro entre 1 e 500.');
  const sheet = SpreadsheetApp.getActive().getSheetByName(NOME_ABA_INVENTARIO_TEMP);
  if (!sheet) { limparEstadoOrfaoInventario_(); throw new Error('A aba temporária foi removida. Inicie um novo inventário pela planilha.'); }
  const itens = [], valores = sheet.getRange(2, 1, Math.max(sheet.getLastRow() - 1, 0), 5).getDisplayValues();
  if (!especial && valores.some(function (r) { return String(r[4] || '').trim().toLowerCase() === patrimonio.toLowerCase(); })) throw new Error('O patrimônio ' + patrimonio + ' já foi informado neste inventário. Procure o gestor do inventário antes de continuar.');
  const rows = [];
  for (let n = 0; n < quantidade; n++) {
    const row = [String(dados.tipo).trim(), String(dados.modelo).trim(), String(dados.local).trim(), String(dados.alugado).trim(), patrimonio, String(dados.status).trim(), new Date()];
    rows.push(row); itens.push({tipo: row[0], modelo: row[1], local: row[2], patrimonio: row[4], status: row[5]});
  }
  const inicio = Math.max(sheet.getLastRow() + 1, 3);
  sheet.getRange(inicio, 1, rows.length, 7).setValues(rows);
  adicionarRegistroSessaoInventario_(dados, false, quantidade);
  try { CacheService.getDocumentCache().remove('INVENTARIO_OPCOES_V2'); } catch (e) {}
  return {success: true, atualizado: false, quantidade: quantidade, itens: itens};
}

function gravarRelatorioInventario_(linhas) {
  const ss = SpreadsheetApp.getActive();
  let rel = ss.getSheetByName('Relatório Inventário');
  if (!rel) rel = ss.insertSheet('Relatório Inventário');
  rel.clear();
  const agora = Utilities.formatDate(new Date(), Session.getScriptTimeZone() || 'America/Sao_Paulo', 'dd/MM/yyyy HH:mm:ss');
  rel.getRange(1, 1, 1, 4).merge().setValue('RELATÓRIO DO INVENTÁRIO — ' + agora).setFontWeight('bold').setFontSize(14).setBackground('#1f4e78').setFontColor('#ffffff');
  rel.getRange(3, 1, 1, 8).setValues([['Ação', 'Patrimônio', 'Tipo', 'Modelo', 'Local', 'Alugado', 'Status', 'Observação']]).setFontWeight('bold').setBackground('#d9eaf7');
  if (linhas.length) rel.getRange(4, 1, linhas.length, 8).setValues(linhas);
  rel.setFrozenRows(3); [90,140,140,150,180,90,120,360].forEach(function (w, i) { rel.setColumnWidth(i + 1, w); });
  return rel.getName();
}

function aplicarFiltroInventario_(sheet, headerRow, ncol) {
  const atual = sheet.getFilter(); if (atual) atual.remove();
  const ultima = Math.max(sheet.getLastRow(), headerRow + 1);
  sheet.getRange(headerRow, 1, ultima - headerRow + 1, ncol).createFilter();
}

function ajustarLargurasInventario_(sheet) {
  [140, 180, 180, 80, 125, 110, 125, 125, 150].forEach(function (w, i) { sheet.setColumnWidth(i + 1, w); });
}

function restaurarBackupInventario_() {
  const ss = SpreadsheetApp.getActive(), principal = ss.getSheetByName(NOME_ABA_INVENTARIO), backup = ss.getSheetByName('_BACKUP_INVENTARIO');
  if (!principal || !backup) return false;
  if (String(principal.getRange(1, 1).getDisplayValue()).indexOf('INVENTÁRIO EM ANDAMENTO') === 0) { try { principal.deleteRow(1); } catch (e) {} }
  const rows = backup.getLastRow(), cols = backup.getLastColumn();
  if (principal.getMaxRows() < rows) principal.insertRowsAfter(principal.getMaxRows(), rows - principal.getMaxRows());
  if (principal.getMaxColumns() < cols) principal.insertColumnsAfter(principal.getMaxColumns(), cols - principal.getMaxColumns());
  principal.getRange(1, 1, Math.max(principal.getMaxRows(), rows), Math.max(principal.getMaxColumns(), cols)).clear({contentsOnly: false});
  backup.getRange(1, 1, rows, cols).copyTo(principal.getRange(1, 1, rows, cols), SpreadsheetApp.CopyPasteType.PASTE_NORMAL, false);
  principal.showRows(1, principal.getMaxRows());
  return true;
}

function removerRegistroInventarioSessao(patrimonio) {
  if (!PropertiesService.getDocumentProperties().getProperty('INVENTARIO_EM_ANDAMENTO')) throw new Error('Não há inventário em andamento.');
  const chave = normalizarPatrimonioInventario_(patrimonio).toLowerCase();
  const lista = obterRegistrosInventarioSessao().filter(function (x) { return normalizarPatrimonioInventario_(x.patrimonio).toLowerCase() !== chave; });
  PropertiesService.getDocumentProperties().setProperty('INVENTARIO_SESSAO_REGISTROS', JSON.stringify(lista));
  const temp = SpreadsheetApp.getActive().getSheetByName(NOME_ABA_INVENTARIO_TEMP);
  if (temp && temp.getLastRow() > 2) {
    const rows = temp.getRange(3, 1, temp.getLastRow() - 2, 7).getDisplayValues();
    for (let i = rows.length - 1; i >= 0; i--) if (normalizarPatrimonioInventario_(rows[i][4]).toLowerCase() === chave) temp.deleteRow(i + 3);
  }
  return {success: true, registros: lista};
}

function cancelarInventarioEquipamentos(origemWeb) {
  const ui = origemWeb ? null : SpreadsheetApp.getUi();
  if (!PropertiesService.getDocumentProperties().getProperty('INVENTARIO_EM_ANDAMENTO')) throw new Error('Não há inventário em andamento.');
  if (!origemWeb) { const ok = ui.alert('Cancelar inventário', 'Todos os registros desta sessão serão descartados. A aba Equipamentos não será alterada. Continuar?', ui.ButtonSet.YES_NO); if (ok !== ui.Button.YES) return; }
  const ss = SpreadsheetApp.getActive(), principal = ss.getSheetByName(NOME_ABA_INVENTARIO), temp = ss.getSheetByName(NOME_ABA_INVENTARIO_TEMP);
  if (principal) { if (String(principal.getRange(1, 1).getDisplayValue()).indexOf('INVENTÁRIO EM ANDAMENTO') === 0) { try { principal.deleteRow(1); } catch (e) {} } principal.showSheet(); principal.setFrozenRows(localizarCabecalhoInventario_(principal)); aplicarFiltroInventario_(principal, localizarCabecalhoInventario_(principal), Math.max(principal.getLastColumn(), 9)); }
  if (principal) principal.getProtections(SpreadsheetApp.ProtectionType.SHEET).forEach(function (x) { if (x.getDescription() === DESCRICAO_PROTECAO_INVENTARIO) x.remove(); });
  if (temp) { temp.getProtections(SpreadsheetApp.ProtectionType.SHEET).forEach(function (x) { if (x.getDescription() === 'PROTECAO_INVENTARIO_TEMP') x.remove(); }); temp.clearContents(); temp.hideSheet(); temp.setTabColor(null); }
  PropertiesService.getDocumentProperties().deleteProperty('INVENTARIO_EM_ANDAMENTO'); PropertiesService.getDocumentProperties().deleteProperty('INVENTARIO_TOKEN'); PropertiesService.getDocumentProperties().deleteProperty('INVENTARIO_SESSAO_REGISTROS');
  removerBackupInventario_();
  try { CacheService.getDocumentCache().remove('INVENTARIO_OPCOES_V2'); } catch (e) {}
  if (principal) { principal.setTabColor(null); principal.getRange(1, 1).clearNote(); }
  return {success: true};
}

function atualizarOpcoesCadastraisInventario_(sheet, cabecalhos, linhas, headerRow) {
  const nomes = [['Tipo', ['Tipo', 'Tipo de equipamento']], ['Modelo', ['Modelo']], ['Local', ['Local', 'Setor']]];
  nomes.forEach(function (item) {
    const col = indiceCabecalhoInventario_(cabecalhos, item[1]); if (col < 0) return;
    const valores = Array.from(new Set(linhas.map(function (r) { return String(r[col] || '').trim(); }).filter(Boolean)));
    if (!valores.length) return;
    const regra = SpreadsheetApp.newDataValidation().requireValueInList(valores, true).setAllowInvalid(false).build();
    sheet.getRange(headerRow + 1, col + 1, Math.max(sheet.getMaxRows() - headerRow, 1), 1).setDataValidation(regra);
  });
}

function copiarFormatoLinhaSemMesclagem_(origem, destino, ncol) {
  for (let c = 1; c <= ncol; c++) {
    try { origem.getCell(1, c).copyTo(destino.getCell(1, c), SpreadsheetApp.CopyPasteType.PASTE_FORMAT, false); } catch (e) { /* célula mesclada: preserva o formato existente */ }
  }
}
function copiarValidacaoColunaSemMesclagem_(origem, destino) {
  try { origem.copyTo(destino, SpreadsheetApp.CopyPasteType.PASTE_DATA_VALIDATION, false); return; } catch (e) {}
  for (let r = 1; r <= destino.getNumRows(); r++) {
    try { origem.copyTo(destino.getCell(r, 1), SpreadsheetApp.CopyPasteType.PASTE_DATA_VALIDATION, false); } catch (e) {}
  }
}

function normalizarValorComparacaoInventario_(valor) {
  return String(valor == null ? '' : valor)
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/\s+/g, ' ').trim().toUpperCase();
}

function obterAlteracoesCadastraisInventario_(antigo, novo, idx) {
  const campos = [
    ['Tipo', idx.tipo],
    ['Modelo', idx.modelo],
    ['Local', idx.local],
    ['Alugado', idx.alugado],
    ['Patrimônio', idx.patrimonio],
    ['Status', idx.status]
  ];
  return campos.filter(function (campo) {
    return normalizarValorComparacaoInventario_(antigo[campo[1]]) !== normalizarValorComparacaoInventario_(novo[campo[1]]);
  }).map(function (campo) {
    const anterior = String(antigo[campo[1]] == null ? '' : antigo[campo[1]]).trim();
    const atual = String(novo[campo[1]] == null ? '' : novo[campo[1]]).trim();
    return campo[0] + ': de "' + (anterior || '(vazio)') + '" para "' + (atual || '(vazio)') + '"';
  });
}

function finalizarInventarioEquipamentos(origemWeb) {
  const ui = origemWeb ? null : SpreadsheetApp.getUi();
  if (!PropertiesService.getDocumentProperties().getProperty('INVENTARIO_EM_ANDAMENTO')) { mostrarMensagem_('Não há inventário em andamento.'); return; }
  if (!origemWeb) { const resposta = ui.alert('Finalizar inventário', 'A aba Equipamentos será atualizada com os itens registrados no inventário. Itens não registrados serão removidos. As colunas de manutenção dos patrimônios existentes serão preservadas. Continuar?', ui.ButtonSet.YES_NO); if (resposta !== ui.Button.YES) return; }
  const ss=SpreadsheetApp.getActive(), principal=ss.getSheetByName(NOME_ABA_INVENTARIO), temp=ss.getSheetByName(NOME_ABA_INVENTARIO_TEMP); if(!principal)throw new Error('A aba Equipamentos não foi encontrada.'); if(!temp){ limparEstadoOrfaoInventario_(); throw new Error('A aba temporária foi removida. O inventário foi encerrado; inicie um novo inventário pela planilha.'); }
  const hrow=localizarCabecalhoInventario_(principal), ncol=Math.max(principal.getLastColumn(), 9), heads=principal.getRange(hrow,1,1,ncol).getDisplayValues()[0];
  const idx={tipo:indiceCabecalhoInventario_(heads,['Tipo','Tipo de equipamento']),modelo:indiceCabecalhoInventario_(heads,['Modelo']),local:indiceCabecalhoInventario_(heads,['Local','Setor']),alugado:indiceCabecalhoInventario_(heads,['Alugado']),patrimonio:indiceCabecalhoInventario_(heads,['Patrimônio','Patrimonio']),status:indiceCabecalhoInventario_(heads,['Status','Situação','Situacao'])};
  if(Object.keys(idx).some(function(k){return idx[k]<0;}))throw new Error('Não foi possível localizar todos os cabeçalhos da aba Equipamentos.');
  const total=principal.getLastRow()-hrow, antigo=total>0?principal.getRange(hrow+1,1,total,ncol).getValues():[], antigoDisplay=total>0?principal.getRange(hrow+1,1,total,ncol).getDisplayValues():[];
  const porPat={}; antigoDisplay.forEach(function(r,i){const k=normalizarPatrimonioInventario_(r[idx.patrimonio]).toLowerCase();if(k&&!porPat[k])porPat[k]=i;});
  const statusRange = total > 0 ? principal.getRange(hrow + 1, idx.status + 1, total, 1) : null;
  const validacaoStatusOriginal = statusRange ? (statusRange.getDataValidations().map(function (r) { return r[0]; }).find(function (x) { return !!x; }) || null) : null;
  const staged=temp.getLastRow()>2?temp.getRange(3,1,temp.getLastRow()-2,6).getDisplayValues():[], novo=[], destinos=[], usados={}, relatorio=[], duplicados=[];
  staged.forEach(function(r){const patr=normalizarPatrimonioInventario_(r[4]); const k=patr.toLowerCase(); const statusR=String(r[5] || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').trim().toUpperCase(); const especial=!k || (statusR==='BACKUP' && /[A-Z]{2,}FECHADO$/.test(patr)); if(!especial && usados[k]){duplicados.push(patr);return;} if(!especial)usados[k]=true; const antigoI=especial?undefined:porPat[k], row=antigoI===undefined?new Array(ncol).fill(''):antigo[antigoI].slice();
  [[idx.tipo,r[0]],[idx.modelo,r[1]],[idx.local,r[2]],[idx.alugado,r[3]],[idx.patrimonio,patr],[idx.status,r[5]]].forEach(function(x){row[x[0]]=x[1];});
  if (antigoI !== undefined) for (let c = 0; c < ncol; c++) if ([idx.tipo,idx.modelo,idx.local,idx.alugado,idx.patrimonio,idx.status].indexOf(c) < 0) row[c] = antigo[antigoI][c];
  novo.push(row); destinos.push(antigoI === undefined ? -1 : antigoI);
    if (antigoI === undefined) {
      relatorio.push(['ADICIONADO', r[4], r[0], r[1], r[2], r[3], r[5], 'Novo equipamento incluído.']);
    } else {
      const alteracoes = obterAlteracoesCadastraisInventario_(antigo[antigoI], row, idx);
      if (alteracoes.length) {
        relatorio.push(['ALTERADO', r[4], r[0], r[1], r[2], r[3], r[5], alteracoes.join(' | ') + '. Manutenção preservada.']);
      } else {
        relatorio.push(['MANTIDO', r[4], r[0], r[1], r[2], r[3], r[5], 'Nenhum dado cadastral foi alterado; equipamento mantido igual ao cadastro anterior.']);
      }
    }
  });
  antigo.forEach(function (r, i) {
    const k = normalizarPatrimonioInventario_(r[idx.patrimonio]).toLowerCase();
    if (!k || usados[k]) return;
    const statusSemAcento = String(r[idx.status] || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').trim().toLowerCase();
    if (statusSemAcento === 'manutencao externa') {
      usados[k] = true;
      novo.push(r.slice());
      destinos.push(i);
      relatorio.push(['MANTIDO', r[idx.patrimonio], r[idx.tipo], r[idx.modelo], r[idx.local], r[idx.alugado], r[idx.status], 'Não informado no inventário; mantido por estar em Manutenção Externa.']);
    } else {
      relatorio.push(['EXCLUÍDO', r[idx.patrimonio], r[idx.tipo], r[idx.modelo], r[idx.local], r[idx.alugado], r[idx.status], 'Não informado no inventário atual.']);
    }
  });
  const conjunto = function (arr, col) { return new Set(arr.map(function (r) { return String(r[col] || '').trim().toLowerCase(); }).filter(Boolean)); };
  const antigosTipo=conjunto(antigoDisplay,idx.tipo), antigosModelo=conjunto(antigoDisplay,idx.modelo), antigosLocal=conjunto(antigoDisplay,idx.local);
  const novosTipo=conjunto(novo,idx.tipo), novosModelo=conjunto(novo,idx.modelo), novosLocal=conjunto(novo,idx.local);
  const nomesOpcao = function (acao, campo, antigos, novos) { antigos.forEach(function (v) { if (!novos.has(v)) relatorio.push([acao, '', campo, v, '', '', '', 'Opção existente antes do inventário e não utilizada no inventário atual.']); }); novos.forEach(function (v) { if (!antigos.has(v)) relatorio.push([acao, '', campo, v, '', '', '', 'Opção criada durante o inventário atual.']); }); };
  nomesOpcao('OPÇÃO NÃO UTILIZADA', 'Tipo', antigosTipo, novosTipo); nomesOpcao('OPÇÃO NÃO UTILIZADA', 'Modelo', antigosModelo, novosModelo); nomesOpcao('OPÇÃO NÃO UTILIZADA', 'Local', antigosLocal, novosLocal);
  duplicados.forEach(function (patr) { relatorio.push(['DUPLICADO', patr, '', '', '', '', '', 'Patrimônio informado mais de uma vez no inventário.']); });
  const faltantes = relatorio.filter(function (r) { return r[0] === 'EXCLUÍDO'; }).length;
  // A remoção é feita por limpeza de conteúdo, sem apagar linhas: isso preserva layout, mesclagens, cores e regras.

  gravarRelatorioInventario_(relatorio);
  const linhasGravacao = novo.length;
  const backupImutavel = ss.getSheetByName('_BACKUP_INVENTARIO');
  try {
    const fontes = [], modeloPorStatus = {};
    antigo.forEach(function (r, i) { const k=String(r[idx.status] || '').trim().toLowerCase(); if (k && modeloPorStatus[k] === undefined) modeloPorStatus[k]=i; });
    novo.forEach(function (r, target) {
      const source = destinos[target] >= 0 ? destinos[target] : (modeloPorStatus[String(r[idx.status] || '').trim().toLowerCase()] !== undefined ? modeloPorStatus[String(r[idx.status] || '').trim().toLowerCase()] : 0);
      fontes.push(source);
    });
    if (hrow + linhasGravacao > principal.getMaxRows()) principal.insertRowsAfter(principal.getMaxRows(), hrow + linhasGravacao - principal.getMaxRows());
    const faixaDados = principal.getRange(hrow + 1, 1, Math.max(total, linhasGravacao), ncol);
    // As validações antigas de Tipo/Modelo/Local podem rejeitar valores do inventário atual.
    // Elas são removidas somente durante a gravação e recriadas com os valores atuais.
    [idx.tipo, idx.modelo, idx.local].forEach(function (col) {
      if (col >= 0) principal.getRange(hrow + 1, col + 1, Math.max(total, linhasGravacao), 1).clearDataValidations();
    });
    if (total > 0) principal.getRange(hrow + 1, 1, total, ncol).clearContent();
    if (linhasGravacao > 0) principal.getRange(hrow + 1, 1, linhasGravacao, ncol).setValues(novo);
    // Formatação geral vem da linha original/modelo, sem importar as validações antigas.
    fontes.forEach(function (source, target) {
      if (backupImutavel) copiarFormatoLinhaSemMesclagem_(backupImutavel.getRange(source + 1, 1, 1, ncol), principal.getRange(hrow + 1 + target, 1, 1, ncol), ncol);
    });
    // Status não é limpo nem recriado: a regra original da coluna F permanece intacta.
    // Apenas as linhas novas, que não possuíam regra antes, recebem uma cópia do modelo.
    const quantidadeNovos = destinos.filter(function (x) { return x < 0; }).length;
    if (backupImutavel && fontes.length && quantidadeNovos > 0) {
      const statusModelo = backupImutavel.getRange(fontes[fontes.length - 1] + 1, idx.status + 1, 1, 1);
      copiarValidacaoColunaSemMesclagem_(statusModelo, principal.getRange(hrow + 1 + total, idx.status + 1, quantidadeNovos, 1));
    }
    // Tipo, Modelo e Local passam a aceitar somente os valores presentes no inventário atual.
    const colunasAtuais = [[idx.tipo, 'Tipo'], [idx.modelo, 'Modelo'], [idx.local, 'Local']];
    colunasAtuais.forEach(function (item) {
      const col=item[0]; if (col < 0 || !linhasGravacao) return;
      const lista=Array.from(new Set(novo.map(function (r) { return String(r[col] || '').trim(); }).filter(Boolean)));
      if (lista.length) principal.getRange(hrow + 1, col + 1, Math.max(principal.getMaxRows() - hrow, 1), 1).setDataValidation(SpreadsheetApp.newDataValidation().requireValueInList(lista, true).setAllowInvalid(false).build());
    });
  } catch (erroGravacao) { restaurarBackupInventario_(); throw erroGravacao; }

  principal.getProtections(SpreadsheetApp.ProtectionType.SHEET).forEach(function(x){if(x.getDescription()===DESCRICAO_PROTECAO_INVENTARIO)x.remove();});
  if (String(principal.getRange(1, 1).getDisplayValue()).indexOf('INVENTÁRIO EM ANDAMENTO') === 0) { try { principal.deleteRow(1); } catch (e) {} } temp.getProtections(SpreadsheetApp.ProtectionType.SHEET).forEach(function(x){if(x.getDescription()==='PROTECAO_INVENTARIO_TEMP')x.remove();}); PropertiesService.getDocumentProperties().deleteProperty('INVENTARIO_EM_ANDAMENTO'); PropertiesService.getDocumentProperties().deleteProperty('INVENTARIO_TOKEN'); PropertiesService.getDocumentProperties().deleteProperty('INVENTARIO_SESSAO_REGISTROS'); removerBackupInventario_(); removerAbaAvisoInventario_(); temp.clearContents(); temp.hideSheet(); temp.setTabColor(null); principal.setTabColor(null); principal.showSheet(); principal.getRange(1,1).clearNote(); principal.setFrozenRows(localizarCabecalhoInventario_(principal)); aplicarFiltroInventario_(principal, localizarCabecalhoInventario_(principal), ncol); mostrarMensagem_('Inventário finalizado. Relatório gerado na aba "Relatório Inventário".');
}
// ============================================================================
function garantirGatilhoDeSincronizacao_() {
  const gatilhos=ScriptApp.getProjectTriggers();
  if(!gatilhos.some(function(t){return t.getHandlerFunction()==='sincronizarPlanilhaComPainel';})) ScriptApp.newTrigger('sincronizarPlanilhaComPainel').forSpreadsheet(SpreadsheetApp.getActive()).onChange().create();
  if(!gatilhos.some(function(t){return t.getHandlerFunction()==='notificarEdicaoPlanilha_';})) ScriptApp.newTrigger('notificarEdicaoPlanilha_').forSpreadsheet(SpreadsheetApp.getActive()).onEdit().create();
  if(!gatilhos.some(function(t){return t.getHandlerFunction()==='notificarGatilhoHorario_';})) ScriptApp.newTrigger('notificarGatilhoHorario_').timeBased().everyMinutes(5).create();
}

// ============================================================================
// GATILHO AUTOMÁTICO — DIÁRIO ÀS ~6H (sempre ativo, sem opção de desligar)
// ============================================================================

// Horário fixo em que os dados dos funcionários são sincronizados com o
// Fluig. Isto é independente dos horários configurados para cada automação
// de chamado no painel — cada automação "Horário programado" roda no seu
// próprio horário via garantirGatilhoExecutorAutomatico_(), a cada minuto.
// O executor continua sendo o Apps Script desta planilha; ele consulta o painel
// para buscar a configuração e lê/escreve os dados nas abas configuradas.
const HORARIOS_SINCRONIZACAO_FUNCIONARIOS = ['06:00'];

/**
 * Garante que TODOS os gatilhos automáticos existam (sync diário ~6h,
 * executor das automações agendadas e onChange da planilha).
 * É idempotente — pode chamar quantas vezes quiser, sem duplicar gatilhos.
 * Chamada automaticamente por configurarPlanilha() e por
 * atualizarFuncionariosCore_() — o usuário não precisa mais ativar nada.
 */
function ativarAtualizacaoDiaria() {
  garantirGatilhoDeSincronizacao_();
  removerGatilhosDeAtualizacao_();
  removerGatilhosDeRetentativa_();
  HORARIOS_SINCRONIZACAO_FUNCIONARIOS.forEach(function (horario) {
    const partes = String(horario).split(':');
    const hora = Math.max(0, Math.min(23, Number(partes[0] || 6)));
    const minuto = Math.max(0, Math.min(59, Number(partes[1] || 0)));
    ScriptApp.newTrigger('atualizarFuncionarios').timeBased().atHour(hora).nearMinute(minuto).everyDays(1).create();
  });
}

function garantirGatilhoDeRetentativa_() {
  const jaExiste = ScriptApp.getProjectTriggers().some(function (trigger) {
    return trigger.getHandlerFunction() === 'tentarAtualizacaoFuncionarios_';
  });
  if (!jaExiste) {
    ScriptApp.newTrigger('tentarAtualizacaoFuncionarios_')
      .timeBased()
      .everyHours(1)
      .create();
  }
}

function removerGatilhosDeRetentativa_() {
  ScriptApp.getProjectTriggers().forEach(function (trigger) {
    if (trigger.getHandlerFunction() === 'tentarAtualizacaoFuncionarios_') {
      ScriptApp.deleteTrigger(trigger);
    }
  });
}

function tentarAtualizacaoFuncionarios_() {
  atualizarFuncionarios();
}

function removerGatilhosDeAtualizacao_() {
  ScriptApp.getProjectTriggers().forEach(function (trigger) {
    if (trigger.getHandlerFunction() === 'atualizarFuncionarios') {
      ScriptApp.deleteTrigger(trigger);
    }
  });
}
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FluigPainel.Models;
using FluigPainel.Services;
using System.Net.Http.Headers;
using System.Threading;
using System.Text.RegularExpressions;

namespace FluigPainel.Controllers
{
    public class HomeController : Controller
    {
        private const string URL_BASE = "https://fluig.hmtj.org.br:8443";
        private const string LOGIN_URL = URL_BASE + "/portal/p/1/j_security_check";
        private const string BPM_API_URL = URL_BASE + "/bpm/api/v1/requests";
        // Hostname puro (sem esquema/porta) do Fluig, usado pelo interceptador JS injetado
        // no navegador para reconhecer URLs que apontam pro Fluig de verdade.
        private static readonly string FLUIG_HOST_JS = new Uri(URL_BASE).Host;

        private static readonly Encoding EncodingLatin1 = Encoding.GetEncoding("ISO-8859-1");
        private static readonly ConcurrentDictionary<string, UserSessionData> _userSessions = new();
        private static readonly ConcurrentDictionary<string, string> _nomesFuncionarios = new();
        private static readonly SemaphoreSlim _nomesFuncionariosLock = new(1, 1);
        private static DateTime _nomesFuncionariosAtualizadoEm = DateTime.MinValue;
        
        // Cache simples para dados que mudam com menos frequência
        private static readonly ConcurrentDictionary<string, CacheItem> _cache = new();

        // Cache de recursos estáticos do Fluig (css, js, fontes, ícones) usados pela tela do
        // chamado. Esses recursos são iguais para qualquer chamado/usuário e a maioria já vem
        // versionada na própria URL (ex.: ?v=1.8.2-251104), o que serve como invalidação natural.
        // Sem isso, cada abertura de chamado refaz dezenas de requisições ao Fluig só para
        // baixar de novo o mesmo bootstrap.css, jquery.js etc.
        private static readonly ConcurrentDictionary<string, RecursoCacheado> _cacheRecursosEstaticos = new();
        private static readonly TimeSpan TtlRecursoEstatico = TimeSpan.FromMinutes(30);
        private static readonly SemaphoreSlim _loginSemaphore = new SemaphoreSlim(1, 1);
        private static readonly AutomaticCallStoreService AutomaticCallStore = new();
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AutomaticCallScheduler _automaticCallScheduler;
        private readonly ILogger<HomeController> _logger;
        private static readonly object _usuarioFluigCacheLock = new();
        private static DadosUsuarioFluigCacheado? _usuarioFluigCache;
        private static string LoginChamado => Environment.GetEnvironmentVariable("LOGIN_AUTO_CHAMADO") ?? "";
        private static string SenhaChamado => Environment.GetEnvironmentVariable("SENHA_AUTO_CHAMADO") ?? "";

        private sealed class DadosUsuarioFluigCacheado
        {
            public string TaskUserId { get; init; } = "";
            public string UsuarioSolicitante { get; init; } = "";
            public string NomeSolicitante { get; init; } = "";
            public DateTime ExpiraEm { get; init; }
        }
        public HomeController(IHttpClientFactory httpClientFactory, AutomaticCallScheduler automaticCallScheduler, ILogger<HomeController> logger) { _httpClientFactory = httpClientFactory; _automaticCallScheduler = automaticCallScheduler; _logger = logger; }
        
        // Usa a configuração padrão
        private static readonly List<UsuarioConfig> UsuariosConfig = ConfiguracaoSistema.UsuariosPadrao;
        private static readonly UsuarioConfig? UsuarioPadrao = UsuariosConfig.FirstOrDefault();

        // Flag para indicar se o Fluig está indisponível
        private static bool _fluigIndisponivel = false;
        private static DateTime _ultimaVerificacaoFluig = DateTime.MinValue;

        #region Ações Principais - OTIMIZADAS COM PARALELISMO

        public async Task<IActionResult> Index(string fila = "TI35")
        {
            var refreshSeconds = ConfiguracaoSistema.RefreshSecondsPadrao;

            if (!string.IsNullOrEmpty(fila))
            {
                ConfiguracaoSistema.FilaPadrao = fila.ToUpper();
            }

            // Verificar se o Fluig está online antes de fazer requisições
            if (!await VerificarFluigOnlineAsync())
            {
                var offlineResponse = new FluigResponseUnificado
                {
                    RefreshSeconds = refreshSeconds,
                    Success = false,
                    UsuarioLogado = "Sistema Integrado",
                    FilaAtual = ConfiguracaoSistema.FilaPadrao,
                    ContagensUsuarios = new List<ContagemUsuario>(),
                    ErrorMessage = "Fluig está indisponível no momento. Tente novamente em alguns instantes.",
                    FluigOffline = true
                };
                return View(offlineResponse);
            }

            var loginCentral = HttpContext.Session.GetString("CentralChamadosLogin");
            var response = new FluigResponseUnificado
            {
                RefreshSeconds = refreshSeconds,
                Success = true,
                UsuarioLogado = "Sistema Integrado",
                FilaAtual = ConfiguracaoSistema.FilaPadrao,
                ContagensUsuarios = new List<ContagemUsuario>()
            };

            try
            {
                // EXECUTAR EM PARALELO - melhorias de performance
                var aguardandoTask = BuscarSolicitacoesAguardandoPoolAsync(ConfiguracaoSistema.FilaPadrao);
                var emAtendimentoTask = BuscarProcessosEmAtendimentoUnificadoAsync();
                var contagensTask = BuscarTodasContagensParaleloAsync();
                await Task.WhenAll(aguardandoTask, emAtendimentoTask, contagensTask);
                
                var aguardandoResult = await aguardandoTask;
                var emAtendimentoResult = await emAtendimentoTask;
                var contagens = await contagensTask;

                // Processar AGUARDANDO
                if (aguardandoResult.Success)
                {
                    foreach (var sol in aguardandoResult.Solicitacoes)
                    {
                        response.AguardandoAtendimento.Add(new Solicitacao
                        {
                            Codigo = sol.Codigo,
                            Detalhe = sol.Detalhe,
                            Solicitante = sol.Solicitante,
                            Setor = sol.Setor,
                            Abertura = sol.Abertura,
                            PrazoTarefa = sol.PrazoTarefa,
                            DataAbertura = sol.DataAbertura,
                            Estado = sol.Estado,
                            TipoAtendimento = "Aguardando"
                        });
                    }
                }
                else
                {
                    response.ErrorMessage = $"Erro ao buscar aguardando: {aguardandoResult.ErrorMessage}";
                    response.Success = false;
                }

                // Processar EM ATENDIMENTO
                if (emAtendimentoResult.Success)
                {
                    foreach (var sol in emAtendimentoResult.Solicitacoes)
                    {
                        if (string.Equals(loginCentral, sol.ResponsavelLogin, StringComparison.OrdinalIgnoreCase))
                            response.ChamadosDoUsuarioLogado.Add(sol);
                        if (!string.Equals(sol.Estado, "Atendimento", StringComparison.OrdinalIgnoreCase) && !string.Equals(sol.Estado, "Atendimento A.T.", StringComparison.OrdinalIgnoreCase))
                            continue;
                        response.EmAtendimento.Add(new Solicitacao
                        {
                            Codigo = sol.Codigo,
                            Detalhe = sol.Detalhe,
                            Solicitante = sol.Solicitante,
                            Setor = sol.Setor,
                            Abertura = sol.Abertura,
                            PrazoTarefa = sol.PrazoTarefa,
                            DataAbertura = sol.DataAbertura,
                            Estado = sol.Estado,
                            Responsavel = sol.Responsavel,
                            ResponsavelLogin = sol.ResponsavelLogin,
                            TipoAtendimento = "EmAtendimento"
                        });
                    }
                }
                else
                {
                    if (response.Success)
                        response.ErrorMessage = $"Erro ao buscar em atendimento: {emAtendimentoResult.ErrorMessage}";
                    else
                        response.ErrorMessage += $"; Erro em atendimento: {emAtendimentoResult.ErrorMessage}";
                    response.Success = false;
                }

                // Adicionar contagens ao response
                response.ContagensUsuarios = contagens;

                // Remover duplicatas usando HashSet (mais eficiente que GroupBy)
                var aguardandoSet = new HashSet<string>();
                response.AguardandoAtendimento = response.AguardandoAtendimento
                    .Where(s => aguardandoSet.Add(s.Codigo))
                    .ToList();

                var emAtendimentoSet = new HashSet<string>();
                response.EmAtendimento = response.EmAtendimento
                    .Where(s => emAtendimentoSet.Add(s.Codigo))
                    .OrderBy(s => int.Parse(s.Codigo))
                    .ToList();

                // Se conseguiu buscar dados, limpa a flag de indisponível
                if (response.Success)
                {
                    _fluigIndisponivel = false;
                }
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.ErrorMessage = $"Erro ao carregar dados: {ex.Message}";
                
                // Marcar Fluig como indisponível se for erro de conexão
                if (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    _fluigIndisponivel = true;
                }
            }

            ViewBag.UsuarioCentral = HttpContext.Session.GetString("CentralChamadosUsuario");
            ViewBag.PodeAcessarCentral = CentralChamadosAutorizada;
            return View(response);
        }

        /// <summary>
        /// Verifica se o Fluig está online com timeout curto
        /// </summary>
        private async Task<bool> VerificarFluigOnlineAsync()
        {
            // Se já verificou há menos de 10 segundos, usa o cache
            if ((DateTime.Now - _ultimaVerificacaoFluig).TotalSeconds < 10)
            {
                return !_fluigIndisponivel;
            }

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                
                // Tenta acessar a página principal do Fluig
                var response = await client.GetAsync($"{URL_BASE}/portal/");
                
                _ultimaVerificacaoFluig = DateTime.Now;
                _fluigIndisponivel = !response.IsSuccessStatusCode;
                
                // Se Fluig está online, limpar cache de sessões para forçar renovação
                if (!_fluigIndisponivel)
                {
                    // Limpa sessões antigas para forçar nova autenticação
                    foreach (var key in _userSessions.Keys.ToList())
                    {
                        if (_userSessions.TryGetValue(key, out var session) && 
                            session.LastActivity < DateTime.Now.AddMinutes(-30))
                        {
                            RemoverSessao(key);
                        }
                    }
                }
                
                return !_fluigIndisponivel;
            }
            catch
            {
                _ultimaVerificacaoFluig = DateTime.Now;
                _fluigIndisponivel = true;
                return false;
            }
        }

        // Buscar todas contagens em PARALELO (hoje e ontem)
        private async Task<List<ContagemUsuario>> BuscarTodasContagensParaleloAsync()
        {
            var contagens = new ConcurrentBag<ContagemUsuario>();
            var tasks = UsuariosConfig.Select(async usuario =>
            {
                try
                {
                    var hoje = await BuscarContagemPorDataComCacheAsync(usuario, DateTime.Now);
                    var ontem = await BuscarContagemPorDataComCacheAsync(usuario, DateTime.Now.AddDays(-1));
                    
                    contagens.Add(new ContagemUsuario
                    {
                        Nome = usuario.Nome,
                        Hoje = hoje,
                        Ontem = ontem
                    });
                }
                catch
                {
                    // Se falhar, adiciona com zeros
                    contagens.Add(new ContagemUsuario
                    {
                        Nome = usuario.Nome,
                        Hoje = 0,
                        Ontem = 0
                    });
                }
            });
            
            await Task.WhenAll(tasks);
            return contagens.ToList();
        }

        [HttpGet]
        public IActionResult TrocarFila(string fila)
        {
            if (string.IsNullOrEmpty(fila)) fila = "TI35";
            ConfiguracaoSistema.FilaPadrao = fila.ToUpper();
            return RedirectToAction("Index");
        }

        /// <summary>
        /// Abre a tela nativa do chamado com a sessão técnica do Fluig no servidor.
        /// A senha nunca é enviada ao navegador; os recursos da tela passam pelo
        /// proxy abaixo para que a sessão criada pelo servidor continue válida.
        /// Configure LOGIN_FLUIG_VISUALIZADOR e SENHA_FLUIG_VISUALIZADOR como variáveis de ambiente.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> VisualizarChamado(string codigo)
        {
            if (!long.TryParse(codigo, out _))
                return BadRequest("Código de chamado inválido.");

            var login = Environment.GetEnvironmentVariable("LOGIN_FLUIG_VISUALIZADOR");
            var senha = Environment.GetEnvironmentVariable("SENHA_FLUIG_VISUALIZADOR");
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(senha))
                return StatusCode(500, "A visualização do Fluig não está configurada no servidor.");

            try
            {
                var usuario = new UsuarioConfig { Login = login, Senha = senha, Nome = "Visualização Fluig" };
                var session = await GetOrCreateSessionAsync(usuario);
                // Tela nativa do Fluig: já traz as abas Formulário, Histórico e Anexos.
                var fluigUri = new Uri($"{URL_BASE}/portal/p/1/pageworkflowview?app_ecm_workflowview_detailsProcessInstanceID={WebUtility.UrlEncode(codigo)}");
                var html = await BaixarFluigAsync(session, fluigUri);

                if (html.Response.StatusCode == HttpStatusCode.Unauthorized || html.Response.StatusCode == HttpStatusCode.Forbidden)
                {
                    RemoverSessao(login);
                    session = await GetOrCreateSessionAsync(usuario);
                    html = await BaixarFluigAsync(session, fluigUri);
                }

                if (!html.Response.IsSuccessStatusCode)
                    return StatusCode((int)html.Response.StatusCode, "Não foi possível carregar a tela do chamado no Fluig.");

                var proxyBase = Url.Action(nameof(FluigProxy), "Home")!;
                var rewritten = ReescreverRecursosFluig(Encoding.UTF8.GetString(html.Body), fluigUri, proxyBase);

                // Dispara em segundo plano, TODOS de uma vez, o download dos recursos (css/js/
                // fontes/ícones) que essa tela vai precisar, sem esperar o navegador pedir um a
                // um. Como o navegador limita quantas conexões simultâneas abre para o mesmo
                // domínio, na primeira abertura ele levava vários "lotes" sequenciais só para
                // buscar recursos que ainda não estavam no cache do servidor. Aquecendo o cache
                // aqui, quando o navegador pedir cada recurso ele já estará pronto.
                _ = PreAquecerRecursosAsync(ExtrairCaminhosRecursos(rewritten), usuario, proxyBase);

                // A página nativa do Fluig traz o cabeçalho/menu/sidebar do portal inteiro em volta
                // do widget do chamado. Extraímos só o "slot" que contém o widget (Formulário,
                // Informações, Histórico e Anexos) e descartamos o resto da página.
                var telaChamado = ExtrairTelaChamado(rewritten);
                var finalHtml = telaChamado != null
                    ? MontarPaginaMinima(rewritten, telaChamado)
                    : OcultarShellFluig(rewritten); // estrutura inesperada: rede de segurança

                // Título e ícone da aba próprios do visualizador, para não confundir com o
                // Fluig de verdade (a página nativa costuma trazer título/favicon do Fluig).
                finalHtml = AplicarIdentidadeVisualizador(finalHtml, codigo);

                return Content(finalHtml, "text/html; charset=utf-8");
            }
            catch (Exception ex)
            {
                return StatusCode(502, $"Erro ao abrir a tela do chamado no Fluig: {ex.Message}");
            }
        }

        /// <summary>
        /// Consulta o dataset ds_funcionario_dados_adicionais do Fluig e devolve só os
        /// campos usados pela planilha de funcionários (CHAPA, FUNCIONARIO, CPF, SETOR,
        /// SITUACAO, DATAADMISSAO, DATADEMISSAO), já em JSON simples.
        ///
        /// Existe porque o Google Apps Script (UrlFetchApp) não consegue negociar TLS
        /// diretamente com o servidor do Fluig (erro de SSL persistente mesmo com
        /// validateHttpsCertificates:false). Este endpoint roda no servidor .NET, que já
        /// tem a flexibilidade de TLS necessária (ServerCertificateCustomValidationCallback),
        /// e devolve os dados por um HTTPS "normal" que o Apps Script consegue consumir.
        ///
        /// Protegido por chave simples: configure FUNCIONARIOS_API_KEY nas variáveis de
        /// ambiente do servidor e mande o mesmo valor no header "X-Api-Key" a cada chamada.
        /// Reaproveita o login configurado em LOGIN_FLUIG_VISUALIZADOR / SENHA_FLUIG_VISUALIZADOR.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Funcionarios(string codFilial = "35")
        {
            var apiKeyEsperada = Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKeyEsperada))
            {
                var apiKeyRecebida = Request.Headers["X-Api-Key"].ToString();
                if (!string.Equals(apiKeyRecebida, apiKeyEsperada, StringComparison.Ordinal))
                    return Unauthorized();
            }

            var login = Environment.GetEnvironmentVariable("LOGIN_FLUIG_VISUALIZADOR");
            var senha = Environment.GetEnvironmentVariable("SENHA_FLUIG_VISUALIZADOR");
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(senha))
                return StatusCode(500, "O acesso ao Fluig não está configurado no servidor (LOGIN_FLUIG_VISUALIZADOR/SENHA_FLUIG_VISUALIZADOR).");

            try
            {
                var usuario = new UsuarioConfig { Login = login, Senha = senha, Nome = "Consulta de Funcionários" };
                var session = await GetOrCreateSessionAsync(usuario);

                var datasetUri = new Uri(URL_BASE +
                    "/api/public/ecm/dataset/search?datasetId=ds_funcionario_dados_adicionais&filterFields=CODFILIAL," +
                    Uri.EscapeDataString(codFilial));

                var (response, body) = await EncaminharFluigAsync(session, datasetUri, "GET", null);

                if (!response.IsSuccessStatusCode)
                {
                    // Sessão pode ter expirado — renova uma vez e tenta de novo.
                    RemoverSessao(login);
                    session = await GetOrCreateSessionAsync(usuario);
                    (response, body) = await EncaminharFluigAsync(session, datasetUri, "GET", null);
                }

                if (!response.IsSuccessStatusCode)
                    return StatusCode((int)response.StatusCode, "Erro ao consultar o dataset do Fluig.");

                var json = Encoding.UTF8.GetString(body);

                // IMPORTANTE: por padrão o Newtonsoft.Json detecta textos parecidos com
                // datas (ex.: "2025-05-09T00:00:00-03:00") e já converte pra DateTime,
                // perdendo o formato original quando volta pra string (ex.: vira
                // "5/9/2025 3:00:00 AM"). Isso quebrava a leitura de data no Apps Script.
                // DateParseHandling.None mantém tudo como texto puro, do jeito que o
                // Fluig mandou.
                Newtonsoft.Json.Linq.JObject raiz;
                using (var stringReader = new StringReader(json))
                using (var jsonReader = new Newtonsoft.Json.JsonTextReader(stringReader) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                {
                    raiz = Newtonsoft.Json.Linq.JObject.Load(jsonReader);
                }
                var content = raiz?["content"] as Newtonsoft.Json.Linq.JArray;

                var resultado = new List<object>();
                if (content != null)
                {
                    foreach (var item in content)
                    {
                        string? ObterTexto(string campo)
                        {
                            var valor = item[campo]?.ToString();
                            return string.IsNullOrWhiteSpace(valor) ||
                                   string.Equals(valor, "undefined", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(valor, "null", StringComparison.OrdinalIgnoreCase)
                                ? null : valor;
                        }

                        resultado.Add(new
                        {
                            chapa = ObterTexto("CHAPA") ?? "",
                            funcionario = ObterTexto("FUNCIONARIO") ?? "",
                            cpf = ObterTexto("CPF") ?? "",
                            funcao = ObterTexto("FUNCAO") ?? "",
                            setor = ObterTexto("SETOR") ?? "",
                            situacao = ObterTexto("SITUACAO") ?? "",
                            dataAdmissao = ObterTexto("DATAADMISSAO"),
                            dataDemissao = ObterTexto("DATADEMISSAO"),
                        });
                    }
                }

                return Json(resultado);
            }
            catch (Exception ex)
            {
                return StatusCode(502, $"Erro ao consultar funcionários no Fluig: {ex.Message}");
            }
        }

        private static string EncontrarNumeroChamado(Newtonsoft.Json.Linq.JToken? token)
        {
            if (token == null) return "";
            var nomes = new[] { "processInstanceId", "processInstanceID", "requestNumber", "numeroChamado", "codigo", "documentId", "id" };
            if (token is Newtonsoft.Json.Linq.JObject obj)
            {
                foreach (var nome in nomes)
                {
                    var propriedade = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, nome, StringComparison.OrdinalIgnoreCase));
                    if (propriedade != null && !string.IsNullOrWhiteSpace(propriedade.Value.ToString())) return propriedade.Value.ToString();
                }
                foreach (var filho in obj.Properties())
                {
                    var encontrado = EncontrarNumeroChamado(filho.Value);
                    if (!string.IsNullOrWhiteSpace(encontrado)) return encontrado;
                }
            }
            else if (token is Newtonsoft.Json.Linq.JArray array)
            {
                foreach (var filho in array) { var encontrado = EncontrarNumeroChamado(filho); if (!string.IsNullOrWhiteSpace(encontrado)) return encontrado; }
            }
            return "";
        }

        private void RegistrarHistoricoChamado(string configId, string configNome, byte[] requestBody, byte[] responseBody)
        {
            try
            {
                var request = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(Encoding.UTF8.GetString(requestBody));
                var response = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(Encoding.UTF8.GetString(responseBody));
                var formData = request?["formData"] as Newtonsoft.Json.Linq.JArray;
                var titulo = formData?.FirstOrDefault(x => string.Equals((string?)x["name"], "Titulo_54", StringComparison.OrdinalIgnoreCase))?["value"]?.ToString() ?? "";
                var numero = EncontrarNumeroChamado(response);
                var descricao = formData?.FirstOrDefault(x => string.Equals((string?)x["name"], "DescriodoChamado_53", StringComparison.OrdinalIgnoreCase))?["value"]?.ToString() ?? "";
                var status = response?.Properties().FirstOrDefault(p => string.Equals(p.Name, "status", StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, "stateDescription", StringComparison.OrdinalIgnoreCase))?.Value?.ToString();
                var store = AutomaticCallStore.Read();
                store.Historico.Add(new AutomaticCallHistory { ConfigId = configId, ConfigNome = configNome, NumeroChamado = numero, Referencia = titulo, AbertoEm = DateTime.Now, Status = string.IsNullOrWhiteSpace(status) ? "Aberto" : status, Mensagem = descricao });
                AutomaticCallStore.Save(store);
            }
            catch
            {
                // O histórico não pode desfazer um chamado já criado no Fluig.
            }
        }

        private bool CentralChamadosAutorizada => HttpContext.Session.GetString("CentralChamadosAutorizada") == "1";

        [HttpGet]
        public async Task<IActionResult> CentralChamados()
        {
            if (!CentralChamadosAutorizada)
                return View("CentralChamadosLogin");
            await _automaticCallScheduler.SincronizarPlanilhaAgoraAsync(HttpContext.RequestAborted);
            var store = AutomaticCallStore.ReadAndCleanHistory();
            return View(new CentralChamadosViewModel
            {
                Configuracoes = store.Configuracoes,
                Fontes = store.Fontes,
                Regras = store.Regras,
                Historico = store.Historico
            });
        }

        [HttpPost]
        public IActionResult CentralChamadosLogin(string usuario, string senha)
        {
            var esperadoUsuario = LoginChamado;
            var esperadaSenha = SenhaChamado;
            var usuarioFluig = ConfiguracaoSistema.UsuariosPadrao.FirstOrDefault(x => x.Login == usuario && x.Senha == senha);
            var eAdministradorAutomacoes = !string.IsNullOrWhiteSpace(esperadoUsuario) && usuario == esperadoUsuario && senha == esperadaSenha;
            var autorizado = usuarioFluig != null || eAdministradorAutomacoes;
            if (!autorizado)
            {
                ViewBag.Error = "Usuário ou senha inválidos.";
                return View("CentralChamadosLogin");
            }
            if (usuarioFluig?.Papel == "Analista" || eAdministradorAutomacoes)
                HttpContext.Session.SetString("CentralChamadosAutorizada", "1");
            else
                HttpContext.Session.Remove("CentralChamadosAutorizada");
            HttpContext.Session.SetString("CentralChamadosUsuario", usuarioFluig?.Nome ?? usuario);
            HttpContext.Session.SetString("CentralChamadosLogin", usuario);
            HttpContext.Session.SetString("CentralChamadosPapel", usuarioFluig?.Papel ?? "Automação");
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Endpoint genérico consultado pelo Apps Script: devolve TODAS as
        /// automações de chamado ativas configuradas na Central, já resolvidas
        /// (aba, condições da regra, coluna do número do chamado, título,
        /// descrição, categoria/subcategoria/prazo). Qualquer automação criada
        /// ou editada no painel aparece aqui automaticamente — não é preciso
        /// tocar em código para uma nova regra passar a funcionar.
        /// </summary>
        [HttpPost]
        public IActionResult RestaurarConfiguracoesAutomaticas([FromBody] AutomaticCallStore backup)
        {
            var apiKeyEsperada = Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKeyEsperada) && !string.Equals(Request.Headers["X-Api-Key"].ToString(), apiKeyEsperada, StringComparison.Ordinal)) return Unauthorized();
            if (backup == null) return BadRequest();
            var store = AutomaticCallStore.Read();
            store.Exclusoes ??= new();
            foreach (var idExcluido in backup.Exclusoes ?? new()) { if (!store.Exclusoes.Contains(idExcluido)) store.Exclusoes.Add(idExcluido); store.Configuracoes.RemoveAll(x => x.Id == idExcluido); store.Regras.RemoveAll(x => x.Id == idExcluido); }
            foreach (var fonte in backup.Fontes ?? new()) { if (!store.Fontes.Any(x => x.Id == fonte.Id)) store.Fontes.Add(fonte); }
            foreach (var regra in backup.Regras ?? new()) { var atual = store.Regras.FirstOrDefault(x => x.Id == regra.Id); if (atual == null) store.Regras.Add(regra); else { atual.Nome = regra.Nome; atual.Descricao = regra.Descricao; atual.FonteId = regra.FonteId; atual.AbaFonte = regra.AbaFonte; atual.Condicoes = regra.Condicoes ?? new(); atual.Combinacao = regra.Combinacao; atual.Ativa = regra.Ativa; } }
            foreach (var config in backup.Configuracoes ?? new()) { if (store.Exclusoes.Contains(config.Id)) continue; var atual = store.Configuracoes.FirstOrDefault(x => x.Id == config.Id); if (atual == null) store.Configuracoes.Add(config); else { atual.Nome = config.Nome; atual.Ativo = config.Ativo; atual.ModoGatilho = config.ModoGatilho; atual.Periodicidade = config.Periodicidade; atual.Horarios = config.Horarios ?? new(); atual.DiaSemana = config.DiaSemana; atual.DiaMes = config.DiaMes; atual.MesAno = config.MesAno; atual.RegraId = config.RegraId; atual.Categoria = config.Categoria; atual.Subcategoria = config.Subcategoria; atual.PrazoHoras = config.PrazoHoras; atual.Titulo = config.Titulo; atual.Descricao = config.Descricao; atual.ColunaNumeroChamado = config.ColunaNumeroChamado; atual.ColunaNumeroChamadoFonteId = config.ColunaNumeroChamadoFonteId; atual.ColunaNumeroChamadoAba = config.ColunaNumeroChamadoAba; } }
            AutomaticCallStore.Save(store);
            return Json(new { success = true, configuracoes = store.Configuracoes.Count, regras = store.Regras.Count });
        }

        [HttpGet]
        public async Task<IActionResult> DadosUsuarioFluigAutomatico()
        {
            var esperado = Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY") ?? Environment.GetEnvironmentVariable("PLANILHA_SYNC_TOKEN") ?? "";
            if (!string.IsNullOrWhiteSpace(esperado) && !string.Equals(Request.Headers["X-Api-Key"].ToString(), esperado, StringComparison.Ordinal)) return Unauthorized();
            lock (_usuarioFluigCacheLock)
                if (_usuarioFluigCache != null && _usuarioFluigCache.ExpiraEm > DateTime.UtcNow)
                    return Json(_usuarioFluigCache);

            var login = LoginChamado;
            var senha = SenhaChamado;
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(senha)) return StatusCode(500, "Credenciais do usuário automático não configuradas.");
            try
            {
                var usuario = new UsuarioConfig { Login = login, Senha = senha, Nome = "Identificação automática Fluig" };
                var sessao = await GetOrCreateSessionAsync(usuario);
                var uri = new Uri(URL_BASE + "/ecm/api/rest/ecm/colleague/getColleagueByLogin/?username=" + Uri.EscapeDataString(login));
                var resultado = await EncaminharFluigAsync(sessao, uri, "GET", null);
                if (resultado.Response.StatusCode == HttpStatusCode.Unauthorized || resultado.Response.StatusCode == HttpStatusCode.Forbidden)
                {
                    RemoverSessao(login); sessao = await GetOrCreateSessionAsync(usuario); resultado = await EncaminharFluigAsync(sessao, uri, "GET", null);
                }
                if (!resultado.Response.IsSuccessStatusCode) return StatusCode((int)resultado.Response.StatusCode, Encoding.UTF8.GetString(resultado.Body));
                var json = JObject.Parse(Encoding.UTF8.GetString(resultado.Body));
                var content = json["content"] as JObject;
                var pk = content?["colleaguePK"] as JObject;
                var dados = new DadosUsuarioFluigCacheado
                {
                    TaskUserId = pk?["colleagueId"]?.ToString() ?? "",
                    UsuarioSolicitante = login,
                    NomeSolicitante = content?["colleagueName"]?.ToString() ?? "",
                    ExpiraEm = DateTime.UtcNow.AddHours(12)
                };
                if (string.IsNullOrWhiteSpace(dados.TaskUserId) || string.IsNullOrWhiteSpace(dados.NomeSolicitante)) return StatusCode(502, "getColleagueByLogin não retornou os dados completos do solicitante.");
                lock (_usuarioFluigCacheLock) _usuarioFluigCache = dados;
                return Json(dados);
            }
            catch (Exception ex) { return StatusCode(502, "Não foi possível obter os dados do solicitante no Fluig: " + ex.Message); }
        }

        [HttpPost]
        public async Task<IActionResult> NotificarAutomacao()
        {
            var esperado = Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY") ?? Environment.GetEnvironmentVariable("PLANILHA_SYNC_TOKEN") ?? "";
            if (!string.IsNullOrWhiteSpace(esperado) && !string.Equals(Request.Headers["X-Api-Key"].ToString(), esperado, StringComparison.Ordinal)) return Unauthorized();
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(Request.Body, cancellationToken: HttpContext.RequestAborted);
            var tipo = doc.RootElement.TryGetProperty("tipo", out var t) ? t.GetString() ?? "evento" : "evento";
            var enfileirado = _automaticCallScheduler.EnfileirarNotificacao(tipo, doc.RootElement);
            return StatusCode(202, new { success = enfileirado, enfileirado, tipo, mensagem = "Evento recebido; processamento continuará no programa." });
        }

        [HttpGet]
        public IActionResult ConfiguracoesAutomaticas()
        {
            var apiKeyEsperada = Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKeyEsperada) && !string.Equals(Request.Headers["X-Api-Key"].ToString(), apiKeyEsperada, StringComparison.Ordinal)) return Unauthorized();
            var store = AutomaticCallStore.Read();
            var resultado = store.Configuracoes.Select(config =>
            {
                var regra = store.Regras.FirstOrDefault(x => x.Id == config.RegraId);
                var exigeRegra = string.Equals(config.ModoGatilho, "Evento", StringComparison.OrdinalIgnoreCase);
                var regraIncompleta = regra == null || string.IsNullOrWhiteSpace(regra.AbaFonte) || regra.Condicoes == null || regra.Condicoes.Count == 0;
                var incompleta = (exigeRegra && regraIncompleta) || string.IsNullOrWhiteSpace(config.Titulo) || string.IsNullOrWhiteSpace(config.Descricao);
                return new
                {
                    id = config.Id,
                    regraId = config.RegraId,
                    fonteId = regra?.FonteId ?? config.FonteId,
                    abaFonte = regra?.AbaFonte ?? config.AbaFonte,
                    periodicidade = config.Periodicidade,
                    diaSemana = config.DiaSemana,
                    diaMes = config.DiaMes,
                    mesAno = config.MesAno,
                    nome = config.Nome,
                    ativo = config.Ativo && !incompleta,
                    modoGatilho = config.ModoGatilho,
                    horarios = config.Horarios,
                    aba = regra?.AbaFonte ?? "",
                    condicoes = (regra?.Condicoes ?? new()).Select(c => new { coluna = c.Coluna, operador = c.Operador, tipoValor = c.TipoValor, valor = c.Valor, modoData = c.ModoData, periodoQuantidade = c.PeriodoQuantidade, periodoUnidade = c.PeriodoUnidade }),
                    combinacao = regra?.Combinacao ?? "E",
                    colunaChamado = config.ColunaNumeroChamado,
                    colunaChamadoFonteId = string.IsNullOrWhiteSpace(config.ColunaNumeroChamadoFonteId) ? (regra?.FonteId ?? "") : config.ColunaNumeroChamadoFonteId,
                    colunaChamadoAba = string.IsNullOrWhiteSpace(config.ColunaNumeroChamadoAba) ? (regra?.AbaFonte ?? "") : config.ColunaNumeroChamadoAba,
                    titulo = config.Titulo,
                    descricao = config.Descricao,
                    categoria = config.Categoria,
                    subcategoria = config.Subcategoria,
                    prazoHoras = config.PrazoHoras,
                    incompleta,
                };
            }).ToList();
            return Json(new { configuracoes = resultado, excluidos = store.Exclusoes ?? new List<string>() });
        }

        /// <summary>
        /// Mantido apenas por compatibilidade com versões antigas do Apps
        /// Script. Novas instalações devem usar ConfiguracoesAutomaticas.
        /// </summary>
        [HttpGet]
        public IActionResult ConfiguracaoDesligamento()
        {
            var apiKeyEsperada = Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKeyEsperada) && !string.Equals(Request.Headers["X-Api-Key"].ToString(), apiKeyEsperada, StringComparison.Ordinal)) return Unauthorized();
            var store = AutomaticCallStore.Read();
            var config = store.Configuracoes.FirstOrDefault(x => x.Tipo == "DESLIGAMENTO_FUNCIONARIO" || x.Id == "desligamento-funcionario");
            var regra = config == null ? null : store.Regras.FirstOrDefault(x => x.Id == config.RegraId);
            var condicoes = regra?.Condicoes ?? new();
            var demissao = condicoes.FirstOrDefault(x => x.Coluna.Contains("DEMISSAO", StringComparison.OrdinalIgnoreCase));
            if (config == null || regra == null || string.IsNullOrWhiteSpace(regra.AbaFonte) || demissao == null || string.IsNullOrWhiteSpace(config.Titulo) || string.IsNullOrWhiteSpace(config.Descricao)) return BadRequest(new { error = "A regra de desligamento precisa configurar fonte, aba, colunas, título e descrição no painel." });
            return Json(new { aba = regra.AbaFonte, colunaDemissao = demissao.Coluna, colunaChamado = config.ColunaNumeroChamado,
                    colunaChamadoFonteId = string.IsNullOrWhiteSpace(config.ColunaNumeroChamadoFonteId) ? (regra?.FonteId ?? "") : config.ColunaNumeroChamadoFonteId,
                    colunaChamadoAba = string.IsNullOrWhiteSpace(config.ColunaNumeroChamadoAba) ? (regra?.AbaFonte ?? "") : config.ColunaNumeroChamadoAba, horario = config.Horarios?.FirstOrDefault(), horarios = config.Horarios, titulo = config.Titulo, descricao = config.Descricao });
        }

        [HttpGet]
        public IActionResult DiagnosticoAgendador()
        {
            if (!CentralChamadosAutorizada) return Unauthorized();
            var fusoConfigurado = Environment.GetEnvironmentVariable("TIME_ZONE_ID");
            TimeZoneInfo fuso;
            try { fuso = string.IsNullOrWhiteSpace(fusoConfigurado) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(fusoConfigurado); }
            catch { fuso = TimeZoneInfo.Local; }
            var agoraUtc = DateTime.UtcNow;
            return Json(new { servidor = fuso.Id, agoraLocal = TimeZoneInfo.ConvertTimeFromUtc(agoraUtc, fuso).ToString("yyyy-MM-dd HH:mm:ss"), agoraUtc = agoraUtc.ToString("yyyy-MM-dd HH:mm:ss"), aviso = "O agendamento precisa ser criado no executor (Apps Script ou serviço do servidor); salvar esta tela não cria um gatilho por si só." });
        }

        [HttpPost]
        public IActionResult SairCentralChamados()
        {
            HttpContext.Session.Remove("CentralChamadosAutorizada"); HttpContext.Session.Remove("CentralChamadosUsuario"); HttpContext.Session.Remove("CentralChamadosLogin"); HttpContext.Session.Remove("CentralChamadosPapel");
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public IActionResult SalvarConfiguracaoChamado(AutomaticCallConfig model)
        {
            if (!CentralChamadosAutorizada) return Unauthorized();
            var store = AutomaticCallStore.Read();
            if (string.IsNullOrWhiteSpace(model.Id)) model.Id = Guid.NewGuid().ToString("N");
            if (store.Configuracoes.Any(x => x.Id != model.Id && string.Equals(x.Nome?.Trim(), model.Nome?.Trim(), StringComparison.OrdinalIgnoreCase))) return BadRequest("Já existe uma automação com este nome.");
            // A fonte/aba nunca são escolhidas diretamente no agendamento — elas
            // vêm sempre da regra selecionada, para não haver duas configurações
            // divergentes para a mesma automação.
            var regraSelecionada = store.Regras.FirstOrDefault(x => x.Id == model.RegraId);
            model.FonteId = regraSelecionada?.FonteId ?? "";
            model.AbaFonte = regraSelecionada?.AbaFonte ?? "";
            if (string.IsNullOrWhiteSpace(model.ColunaNumeroChamadoFonteId)) model.ColunaNumeroChamadoFonteId = model.FonteId;
            if (string.IsNullOrWhiteSpace(model.ColunaNumeroChamadoAba)) model.ColunaNumeroChamadoAba = model.AbaFonte;
            var existente = store.Configuracoes.FirstOrDefault(x => x.Id == model.Id);
            if (existente == null) store.Configuracoes.Add(model);
            else
            {
                existente.Nome = model.Nome;
                existente.Ativo = model.Ativo;
                existente.ModoGatilho = model.ModoGatilho;
                existente.Periodicidade = model.Periodicidade;
                existente.Horarios = model.Horarios ?? new();
                existente.DiaSemana = model.DiaSemana;
                existente.DiaMes = model.DiaMes;
                existente.MesAno = model.MesAno;
                existente.FonteId = model.FonteId;
                existente.AbaFonte = model.AbaFonte;
                existente.RegraId = model.RegraId;
                existente.Categoria = model.Categoria;
                existente.Subcategoria = model.Subcategoria;
                existente.PrazoHoras = model.PrazoHoras;
                existente.Titulo = model.Titulo;
                existente.Descricao = model.Descricao;
                existente.ColunaNumeroChamado = model.ColunaNumeroChamado;
                existente.ColunaNumeroChamadoFonteId = model.ColunaNumeroChamadoFonteId;
                existente.ColunaNumeroChamadoAba = model.ColunaNumeroChamadoAba;
                existente.Tipo = model.Tipo;
            }
            AutomaticCallStore.Save(store);
            PublicarConfiguracoesNaPonte(store);
            return RedirectToAction(nameof(CentralChamados));
        }

        private void PublicarConfiguracoesNaPonte(AutomaticCallStore store)
        {
            try
            {
                var url = Environment.GetEnvironmentVariable("PLANILHA_SYNC_WEBAPP_URL");
                if (string.IsNullOrWhiteSpace(url)) return;
                var token = Environment.GetEnvironmentVariable("PLANILHA_SYNC_TOKEN") ?? Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY") ?? "";
                var sep = url.Contains('?') ? "&" : "?";
                var endpoint = url + sep + "token=" + Uri.EscapeDataString(token) + "&acao=publicar";
                var payload = JsonConvert.SerializeObject(store);
                Exception? ultimaFalha = null;
                for (var tentativa = 1; tentativa <= 2; tentativa++)
                {
                    try
                    {
                        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                        using var response = _httpClientFactory.CreateClient().PostAsync(endpoint, content).GetAwaiter().GetResult();
                        var resposta = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (response.IsSuccessStatusCode && resposta.TrimStart().StartsWith("{") && resposta.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase)) return;
                        ultimaFalha = new InvalidOperationException($"Web App respondeu HTTP {(int)response.StatusCode}: {resposta.Substring(0, Math.Min(resposta.Length, 240))}");
                    }
                    catch (Exception ex) { ultimaFalha = ex; }
                    if (tentativa < 2) Thread.Sleep(800);
                }
                // Não interrompe o salvamento local, mas deixa a falha disponível
                // no log do servidor em vez de descartá-la silenciosamente.
                _logger.LogWarning(ultimaFalha, "Falha ao publicar configurações na planilha após duas tentativas.");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Falha ao preparar publicação das configurações na planilha."); }
        }

        [HttpPost]
        public IActionResult SincronizarPlanilha([FromBody] AutomaticCallSource model)
        {
            var apiKey = Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKey) && !string.Equals(Request.Headers["X-Api-Key"].ToString(), apiKey, StringComparison.Ordinal)) return Unauthorized();
            var store = AutomaticCallStore.Read();
            var fonte = store.Fontes.FirstOrDefault() ?? new AutomaticCallSource { Id = "fonte-planilha-principal" };
            fonte.Id = string.IsNullOrWhiteSpace(fonte.Id) ? "fonte-planilha-principal" : fonte.Id;
            fonte.Nome = model.Nome; fonte.Tipo = "Google Sheets"; fonte.Endereco = model.Endereco; fonte.Abas = model.Abas ?? new(); fonte.AbaOuTabela = fonte.Abas.FirstOrDefault() ?? ""; fonte.Ativa = true;
            if (!store.Fontes.Any()) store.Fontes.Add(fonte);
            foreach (var regra in store.Regras.Where(x => string.IsNullOrWhiteSpace(x.FonteId))) regra.FonteId = fonte.Id;
            AutomaticCallStore.Save(store);
            return Json(new { success = true, nome = fonte.Nome, abas = fonte.Abas });
        }

        [HttpPost]
        public IActionResult SalvarFonteChamado(AutomaticCallSource model)
        {
            if (!CentralChamadosAutorizada) return Unauthorized();
            var store = AutomaticCallStore.Read();
            if (string.IsNullOrWhiteSpace(model.Id)) model.Id = Guid.NewGuid().ToString("N");
            var existente = store.Fontes.FirstOrDefault(x => x.Id == model.Id);
            if (existente == null) store.Fontes.Add(model);
            else { existente.Nome = model.Nome; existente.Tipo = model.Tipo; existente.Endereco = model.Endereco; existente.AbaOuTabela = model.AbaOuTabela; existente.Abas = model.Abas ?? new(); existente.Ativa = model.Ativa; }
            AutomaticCallStore.Save(store);
            PublicarConfiguracoesNaPonte(store);
            return RedirectToAction(nameof(CentralChamados));
        }

        [HttpPost]
        public IActionResult SalvarRegraChamado(AutomaticTriggerRule model)
        {
            if (!CentralChamadosAutorizada) return Unauthorized();
            var store = AutomaticCallStore.Read();
            if (string.IsNullOrWhiteSpace(model.Id)) model.Id = Guid.NewGuid().ToString("N");
            if (store.Regras.Any(x => x.Id != model.Id && string.Equals(x.Nome?.Trim(), model.Nome?.Trim(), StringComparison.OrdinalIgnoreCase))) return BadRequest("Já existe uma regra com este nome.");
            var existente = store.Regras.FirstOrDefault(x => x.Id == model.Id);
            model.Combinacao = string.Equals(model.Combinacao, "OU", StringComparison.OrdinalIgnoreCase) ? "OU" : "E";
            if (existente == null) store.Regras.Add(model);
            else { existente.Nome = model.Nome; existente.Descricao = model.Descricao; existente.Condicao = model.Condicao; existente.FonteId = model.FonteId; existente.AbaFonte = model.AbaFonte; existente.Condicoes = model.Condicoes ?? new(); existente.Combinacao = string.Equals(model.Combinacao, "OU", StringComparison.OrdinalIgnoreCase) ? "OU" : "E"; existente.Ativa = model.Ativa; }
            AutomaticCallStore.Save(store);
            PublicarConfiguracoesNaPonte(store);
            return RedirectToAction(nameof(CentralChamados));
        }

        [HttpPost]
        public IActionResult ExcluirConfiguracaoChamado(string id)
        {
            if (!CentralChamadosAutorizada) return Unauthorized();
            var store = AutomaticCallStore.Read();
            store.Exclusoes ??= new();
            if (!store.Exclusoes.Contains(id)) store.Exclusoes.Add(id);
            store.Configuracoes.RemoveAll(x => x.Id == id);
            AutomaticCallStore.Save(store);
            PublicarConfiguracoesNaPonte(store);
            return RedirectToAction(nameof(CentralChamados));
        }

        [HttpPost]
        public IActionResult ExcluirRegraChamado(string id)
        {
            if (!CentralChamadosAutorizada) return Unauthorized();
            var store = AutomaticCallStore.Read();
            var emUso = store.Configuracoes.Any(x => x.RegraId == id);
            if (!emUso) store.Exclusoes ??= new();
            if (!store.Exclusoes.Contains(id)) store.Exclusoes.Add(id);
            store.Regras.RemoveAll(x => x.Id == id);
            AutomaticCallStore.Save(store);
            PublicarConfiguracoesNaPonte(store);
            return RedirectToAction(nameof(CentralChamados));
        }

        /// <summary>
        /// Abre um chamado no Fluig usando a sessão autenticada do servidor.
        /// O Apps Script usa este endpoint (para qualquer automação, não só
        /// desligamento) porque não consegue negociar o TLS legado do Fluig
        /// diretamente. configId/configNome identificam qual automação da
        /// Central de Chamados abriu este chamado, para fins de histórico.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AbrirChamadoAutomatico(string? configId = null, string? configNome = null)
        {
            var apiKeyEsperada = Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKeyEsperada))
            {
                var apiKeyRecebida = Request.Headers["X-Api-Key"].ToString();
                if (!string.Equals(apiKeyRecebida, apiKeyEsperada, StringComparison.Ordinal))
                    return Unauthorized();
            }

            var login = LoginChamado;
            var senha = SenhaChamado;
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(senha))
                return StatusCode(500, "As credenciais LOGIN_AUTO_CHAMADO/SENHA_AUTO_CHAMADO não estão configuradas.");

            try
            {
                using var bodyStream = new MemoryStream();
                await Request.Body.CopyToAsync(bodyStream);
                var body = bodyStream.ToArray();
                var contentType = Request.ContentType ?? "application/json";
                var usuario = new UsuarioConfig { Login = login, Senha = senha, Nome = "Abertura de chamado automático" };
                var session = await GetOrCreateSessionAsync(usuario);
                var targetUri = new Uri(URL_BASE + "/ecm/api/rest/ecm/workflowView/send");
                var result = await EncaminharFluigAsync(session, targetUri, "POST", body, contentType);

                if (result.Response.StatusCode == HttpStatusCode.Unauthorized || result.Response.StatusCode == HttpStatusCode.Forbidden)
                {
                    RemoverSessao(login);
                    session = await GetOrCreateSessionAsync(usuario);
                    result = await EncaminharFluigAsync(session, targetUri, "POST", body, contentType);
                }

                var responseContentType = result.Response.Content.Headers.ContentType?.ToString() ?? "application/json";
                if (!result.Response.IsSuccessStatusCode)
                {
                    Response.StatusCode = (int)result.Response.StatusCode;
                    return File(result.Body, responseContentType);
                }

                RegistrarHistoricoChamado(string.IsNullOrWhiteSpace(configId) ? "desconhecida" : configId, string.IsNullOrWhiteSpace(configNome) ? "Automação não identificada" : configNome, body, result.Body);
                var response = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JToken>(Encoding.UTF8.GetString(result.Body));
                var numeroChamado = EncontrarNumeroChamado(response);
                if (string.IsNullOrWhiteSpace(numeroChamado))
                {
                    var encontrado = System.Text.RegularExpressions.Regex.Match(Encoding.UTF8.GetString(result.Body), @"\b\d{4,}\b");
                    numeroChamado = encontrado.Success ? encontrado.Value : "";
                }
                return Content(JsonConvert.SerializeObject(new { success = true, numeroChamado, fluig = response }), "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(502, $"Erro ao abrir chamado no Fluig: {ex.Message}");
            }
        }

        /// <summary>
        /// Mantido apenas por compatibilidade com versões antigas do Apps
        /// Script — encaminha para o endpoint genérico. Novas instalações
        /// devem usar AbrirChamadoAutomatico diretamente.
        /// </summary>
        [HttpPost]
        public Task<IActionResult> AbrirChamadoDesligamento() => AbrirChamadoAutomatico("desligamento-funcionario", "Desligamento de funcionário");

        [HttpGet]
        public async Task<IActionResult> FluigDataset(string dataset, string? categoria = null)
        {
            if (!CentralChamadosAutorizada) return Unauthorized();
            if (string.IsNullOrWhiteSpace(dataset) || !Regex.IsMatch(dataset, "^[A-Za-z0-9_]+$")) return BadRequest("Dataset inválido.");
            var login = Environment.GetEnvironmentVariable("LOGIN_FLUIG_VISUALIZADOR") ?? Environment.GetEnvironmentVariable("LOGIN_AUTO_CHAMADO");
            var senha = Environment.GetEnvironmentVariable("SENHA_FLUIG_VISUALIZADOR") ?? Environment.GetEnvironmentVariable("SENHA_AUTO_CHAMADO");
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(senha)) return StatusCode(500, "Credenciais do Fluig não configuradas.");
            try
            {
                var constraints = new List<object> { new { _field = "TIPO", _initialValue = "TI", _finalValue = "TI", _type = 1 } };
                if (!string.IsNullOrWhiteSpace(categoria)) constraints.Add(new { _field = "CATEGORIA", _initialValue = categoria, _finalValue = categoria, _type = 1 });
                var payload = JsonConvert.SerializeObject(new { name = dataset, fields = (object?)null, constraints, order = (object?)null });
                var session = await GetOrCreateSessionAsync(new UsuarioConfig { Login = login, Senha = senha, Nome = "Listas de chamados" });
                var result = await EncaminharFluigAsync(session, new Uri(URL_BASE + "/api/public/ecm/dataset/datasets/"), "POST", Encoding.UTF8.GetBytes(payload), "application/json");
                if (result.Response.StatusCode == HttpStatusCode.Unauthorized || result.Response.StatusCode == HttpStatusCode.Forbidden)
                {
                    RemoverSessao(login); session = await GetOrCreateSessionAsync(new UsuarioConfig { Login = login, Senha = senha, Nome = "Listas de chamados" });
                    result = await EncaminharFluigAsync(session, new Uri(URL_BASE + "/api/public/ecm/dataset/datasets/"), "POST", Encoding.UTF8.GetBytes(payload), "application/json");
                }
                if (!result.Response.IsSuccessStatusCode) return StatusCode((int)result.Response.StatusCode, Encoding.UTF8.GetString(result.Body));
                return Content(Encoding.UTF8.GetString(result.Body), "application/json");
            }
            catch (Exception ex) { return StatusCode(502, "Não foi possível carregar a lista do Fluig: " + ex.Message); }
        }

        [HttpGet]
        public async Task<IActionResult> FluigProxy(string path, string? query = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out var parsed))
                return BadRequest("Recurso Fluig inválido.");

            var targetPath = parsed.IsAbsoluteUri ? parsed.PathAndQuery : path;
            if (!targetPath.StartsWith("/", StringComparison.Ordinal)) targetPath = "/" + targetPath;
            if (!string.IsNullOrEmpty(query)) targetPath += (targetPath.Contains('?') ? "&" : "?") + query;

            var cacheKey = "FluigProxy:" + targetPath;
            if (_cacheRecursosEstaticos.TryGetValue(cacheKey, out var emCache) && emCache.Expira > DateTime.Now)
            {
                AplicarCacheControlNavegador(emCache.ContentType, targetPath);
                return File(emCache.Body, emCache.ContentType);
            }

            var login = Environment.GetEnvironmentVariable("LOGIN_FLUIG_VISUALIZADOR");
            var senha = Environment.GetEnvironmentVariable("SENHA_FLUIG_VISUALIZADOR");
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(senha)) return NotFound();

            try
            {
                var usuario = new UsuarioConfig { Login = login, Senha = senha, Nome = "Visualização Fluig" };
                var session = await GetOrCreateSessionAsync(usuario);
                var result = await BaixarFluigAsync(session, new Uri(URL_BASE + targetPath));
                if (result.Response.StatusCode == HttpStatusCode.Unauthorized || result.Response.StatusCode == HttpStatusCode.Forbidden)
                {
                    RemoverSessao(login);
                    session = await GetOrCreateSessionAsync(usuario);
                    result = await BaixarFluigAsync(session, new Uri(URL_BASE + targetPath));
                }
                if (!result.Response.IsSuccessStatusCode) return StatusCode((int)result.Response.StatusCode);

                var contentType = result.Response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                byte[] corpoFinal;
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    return Content(ReescreverRecursosFluig(Encoding.UTF8.GetString(result.Body), new Uri(URL_BASE + targetPath), Url.Action(nameof(FluigProxy), "Home")!), contentType);
                }
                if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) || contentType.Contains("text/css", StringComparison.OrdinalIgnoreCase))
                {
                    corpoFinal = Encoding.UTF8.GetBytes(ReescreverUrlsAbsolutasFluig(Encoding.UTF8.GetString(result.Body), Url.Action(nameof(FluigProxy), "Home")!));
                }
                else
                {
                    corpoFinal = result.Body;
                }

                if (DeveCachearRecurso(contentType, targetPath))
                    _cacheRecursosEstaticos[cacheKey] = new RecursoCacheado { Body = corpoFinal, ContentType = contentType, Expira = DateTime.Now.Add(TtlRecursoEstatico) };

                AplicarCacheControlNavegador(contentType, targetPath);
                return File(corpoFinal, contentType);
            }
            catch (Exception ex)
            {
                return StatusCode(502, ex.Message);
            }
        }

        // Além do cache em memória do servidor, deixa o PRÓPRIO NAVEGADOR guardar esses
        // recursos (css/js/fontes/ícones): assim, ao abrir o segundo, terceiro... chamado do
        // dia, o navegador nem chega a chamar nosso servidor para pedi-los de novo.
        private void AplicarCacheControlNavegador(string contentType, string path)
        {
            if (DeveCachearRecurso(contentType, path))
                Response.Headers["Cache-Control"] = "public, max-age=1800";
        }

        // O JavaScript nativo do Fluig faz chamadas absolutas (por exemplo
        // /ecm/api, /wcm/api e /fonts). Mantemos essas chamadas na sessão
        // autenticada do servidor, sem expor cookies ou tokens ao navegador.
        [AcceptVerbs("GET", "HEAD", "POST", "PUT", "OPTIONS")]
        [Route("/ecm/{**path}")]
        [Route("/api/{**path}")]
        [Route("/portal/{**path}")]
        [Route("/wcm/{**path}")]
        [Route("/resources/{**path}")]
        [Route("/webdesk/{**path}")]
        [Route("/ecm_resources/{**path}")]
        [Route("/ecm_workflowview/{**path}")]
        [Route("/ecm_documentview/{**path}")]
        [Route("/wdg_repositorio/{**path}")]
        [Route("/social/{**path}")]
        [Route("/collaboration/{**path}")]
        [Route("/alertpopover/{**path}")]
        [Route("/usepolicy/{**path}")]
        [Route("/api/public/{**path}")]
        [Route("/style-guide/{**path}")]
        [Route("/responsive_header/{**path}")]
        [Route("/responsive_menu/{**path}")]
        [Route("/images/{**path}")]
        [Route("/fonts/{**path}")]
        [Route("/admin/{**path}")]
        [Route("/globalalertapi/{**path}")]
        [Route("/page-management/{**path}")]
        public async Task<IActionResult> FluigAbsoluteProxy(string path)
        {
            var fullPath = Request.Path.Value ?? "/";
            var query = Request.QueryString.Value?.TrimStart('?');
            var ehGet = Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase);
            var cacheKey = "FluigAbsoluteProxy:" + fullPath + "?" + query;
            if (ehGet && _cacheRecursosEstaticos.TryGetValue(cacheKey, out var emCache) && emCache.Expira > DateTime.Now)
            {
                AplicarCacheControlNavegador(emCache.ContentType, fullPath);
                return File(emCache.Body, emCache.ContentType);
            }

            var login = Environment.GetEnvironmentVariable("LOGIN_FLUIG_VISUALIZADOR");
            var senha = Environment.GetEnvironmentVariable("SENHA_FLUIG_VISUALIZADOR");
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(senha)) return NotFound();

            try
            {
                var usuario = new UsuarioConfig { Login = login, Senha = senha, Nome = "Visualização Fluig" };
                var session = await GetOrCreateSessionAsync(usuario);
                byte[]? requestBody = null;
                if (Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) || Request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    using var bodyStream = new MemoryStream();
                    await Request.Body.CopyToAsync(bodyStream);
                    requestBody = bodyStream.ToArray();
                }
                var targetUri = new Uri(URL_BASE + fullPath + (string.IsNullOrEmpty(query) ? "" : "?" + query));
                var result = await EncaminharFluigAsync(session, targetUri, Request.Method, requestBody);
                if (result.Response.StatusCode == HttpStatusCode.Unauthorized || result.Response.StatusCode == HttpStatusCode.Forbidden)
                {
                    RemoverSessao(login);
                    session = await GetOrCreateSessionAsync(usuario);
                    result = await EncaminharFluigAsync(session, targetUri, Request.Method, requestBody);
                }
                if (!result.Response.IsSuccessStatusCode) return StatusCode((int)result.Response.StatusCode);
                var contentType = result.Response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    return Content(ReescreverRecursosFluig(Encoding.UTF8.GetString(result.Body), new Uri(URL_BASE + fullPath), Url.Action(nameof(FluigProxy), "Home")!), contentType);

                byte[] corpoFinal;
                if (contentType.Contains("text/css", StringComparison.OrdinalIgnoreCase))
                {
                    corpoFinal = Encoding.UTF8.GetBytes(ReescreverRecursosFluig(Encoding.UTF8.GetString(result.Body), new Uri(URL_BASE + fullPath), Url.Action(nameof(FluigProxy), "Home")!));
                }
                // As abas de Histórico/Anexos (e o iframe do Formulário) são carregadas por chamadas
                // ajax que retornam JSON/JS com URLs absolutas do Fluig (ex.: /webdesk/streamcontrol/...).
                // Sem reescrever essas URLs, o navegador tenta abrir o host interno do Fluig diretamente
                // e a conexão é recusada. Por isso essas respostas também passam pelo proxy.
                else if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                    || contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
                {
                    corpoFinal = Encoding.UTF8.GetBytes(ReescreverUrlsAbsolutasFluig(Encoding.UTF8.GetString(result.Body), Url.Action(nameof(FluigProxy), "Home")!));
                }
                else
                {
                    corpoFinal = result.Body;
                }

                if (ehGet && DeveCachearRecurso(contentType, fullPath))
                    _cacheRecursosEstaticos[cacheKey] = new RecursoCacheado { Body = corpoFinal, ContentType = contentType, Expira = DateTime.Now.Add(TtlRecursoEstatico) };

                if (ehGet) AplicarCacheControlNavegador(contentType, fullPath);
                return File(corpoFinal, contentType);
            }
            catch (Exception ex)
            {
                return StatusCode(502, ex.Message);
            }
        }

        private async Task<(HttpResponseMessage Response, byte[] Body)> BaixarFluigAsync(UserSessionData session, Uri uri)
        {
            return await EncaminharFluigAsync(session, uri, HttpMethod.Get.Method, null);
        }

        #region Pré-aquecimento de recursos (reduz o tempo da primeira abertura)

        // Encontra, no HTML já reescrito, todos os "?path=...&query=..." que apontam para o
        // FluigProxy — ou seja, todo recurso (css, js, fonte, ícone) que o navegador vai pedir
        // em seguida — para podermos buscá-los todos de uma vez, em paralelo, antes mesmo do
        // navegador começar a pedir um por um.
        private static readonly Regex RecursoProxyRegex = new(
            "\\?path=(?<path>[^&\"'\\s)]+)(?:&query=(?<query>[^&\"'\\s)]+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static IEnumerable<string> ExtrairCaminhosRecursos(string html)
        {
            var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in RecursoProxyRegex.Matches(html))
            {
                var path = Uri.UnescapeDataString(m.Groups["path"].Value);
                var alvo = m.Groups["query"].Success
                    ? path + (path.Contains('?') ? "&" : "?") + Uri.UnescapeDataString(m.Groups["query"].Value)
                    : path;
                if (vistos.Add(alvo))
                    yield return alvo;
            }
        }

        // Versão do download que não depende do HttpContext da requisição (roda em segundo
        // plano, possivelmente depois que a resposta original já foi enviada ao navegador).
        private static async Task<(HttpResponseMessage Response, byte[] Body)> BuscarRecursoParaCacheAsync(UserSessionData session, Uri uri)
        {
            var client = session.Client ??= CriarClienteFluig(session.CookieContainer);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(session.JwtToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.JwtToken);
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsByteArrayAsync();
            return (response, body);
        }

        private async Task PreAquecerRecursosAsync(IEnumerable<string> caminhos, UsuarioConfig usuario, string proxyBase)
        {
            using var limitador = new SemaphoreSlim(15);
            var tarefas = caminhos.Select(async targetPath =>
            {
                var cacheKey = "FluigProxy:" + targetPath;
                if (_cacheRecursosEstaticos.TryGetValue(cacheKey, out var existente) && existente.Expira > DateTime.Now)
                    return;

                await limitador.WaitAsync();
                try
                {
                    var session = await GetOrCreateSessionAsync(usuario);
                    var (response, body) = await BuscarRecursoParaCacheAsync(session, new Uri(URL_BASE + targetPath));
                    if (!response.IsSuccessStatusCode) return;

                    var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                    byte[] corpoFinal;
                    if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                        corpoFinal = Encoding.UTF8.GetBytes(ReescreverRecursosFluig(Encoding.UTF8.GetString(body), new Uri(URL_BASE + targetPath), proxyBase));
                    else if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                        || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                        || contentType.Contains("text/css", StringComparison.OrdinalIgnoreCase))
                        corpoFinal = Encoding.UTF8.GetBytes(ReescreverUrlsAbsolutasFluig(Encoding.UTF8.GetString(body), proxyBase));
                    else
                        corpoFinal = body;

                    if (DeveCachearRecurso(contentType, targetPath))
                        _cacheRecursosEstaticos[cacheKey] = new RecursoCacheado { Body = corpoFinal, ContentType = contentType, Expira = DateTime.Now.Add(TtlRecursoEstatico) };
                }
                catch
                {
                    // Pré-aquecimento é best-effort: se falhar, o navegador vai buscar esse
                    // recurso normalmente pelo FluigProxy, então não há nada a fazer aqui.
                }
                finally
                {
                    limitador.Release();
                }
            });

            await Task.WhenAll(tarefas);
        }

        #endregion

        private static HttpClient CriarClienteFluig(CookieContainer cookieContainer)
        {
            var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = cookieContainer,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, errors) => true,
                // Reaproveita conexões TCP/TLS já abertas para o Fluig entre requisições
                // desta sessão, em vez de negociar uma conexão nova a cada recurso.
                // A tela do chamado carrega dezenas de recursos (css/js/fontes/ícones) de uma
                // vez só; com um limite baixo aqui, o próprio HttpClient enfileira essas
                // requisições internamente mesmo que o navegador as tenha disparado em paralelo.
                MaxConnectionsPerServer = 60
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", URL_BASE);
            client.DefaultRequestHeaders.Referrer = new Uri(URL_BASE + "/portal/p/1/pageworkflowview");
            return client;
        }

        private async Task<(HttpResponseMessage Response, byte[] Body)> EncaminharFluigAsync(UserSessionData session, Uri uri, string method, byte[]? requestBody, string? requestContentType = null)
        {
            var client = session.Client ??= CriarClienteFluig(session.CookieContainer);

            using var request = new HttpRequestMessage(new HttpMethod(method), uri);
            if (Request.Headers.TryGetValue("X-Requested-With", out var requestedWith))
                request.Headers.TryAddWithoutValidation("X-Requested-With", requestedWith.ToString());
            if (!string.IsNullOrEmpty(session.JwtToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.JwtToken);
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) || method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
            {
                request.Content = new ByteArrayContent(requestBody ?? Array.Empty<byte>());
                // Preserva application/x-www-form-urlencoded e os parâmetros de charset
                // enviados pelo jQuery; MediaTypeHeaderValue puro rejeita alguns deles.
                if (!string.IsNullOrWhiteSpace(requestContentType ?? Request.ContentType))
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", requestContentType ?? Request.ContentType!);
            }
            var response = await client.SendAsync(request);
            return (response, await response.Content.ReadAsByteArrayAsync());
        }

        private string ReescreverRecursosFluig(string html, Uri pageUri, string proxyBase)
        {
            var pattern = "(?<attr>\\b(?:src|href|action)\\s*=\\s*[\\\"'])(?<url>[^\\\"']+)(?<end>[\\\"'])";
            var rewritten = Regex.Replace(html, pattern, match =>
            {
                var raw = WebUtility.HtmlDecode(match.Groups["url"].Value);
                if (raw.StartsWith("#") || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return match.Value;
                if (!Uri.TryCreate(pageUri, raw, out var resource) || resource.Host != new Uri(URL_BASE).Host) return match.Value;
                var proxyUrl = proxyBase + "?path=" + Uri.EscapeDataString(resource.AbsolutePath) + (string.IsNullOrEmpty(resource.Query) ? "" : "&query=" + Uri.EscapeDataString(resource.Query.TrimStart('?')));
                return match.Groups["attr"].Value + proxyUrl + match.Groups["end"].Value;
            }, RegexOptions.IgnoreCase);

            var cssUrlPattern = "url\\(\\s*(?<quote>[\\\"']?)(?<url>[^)\\\"']+)\\k<quote>\\s*\\)";
            var cssRewritten = Regex.Replace(rewritten, cssUrlPattern, match =>
            {
                var raw = match.Groups["url"].Value.Trim();
                if (raw.StartsWith("#") || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return match.Value;
                if (!Uri.TryCreate(pageUri, raw, out var resource) || resource.Host != new Uri(URL_BASE).Host) return match.Value;
                var proxyUrl = proxyBase + "?path=" + Uri.EscapeDataString(resource.AbsolutePath) + (string.IsNullOrEmpty(resource.Query) ? "" : "&query=" + Uri.EscapeDataString(resource.Query.TrimStart('?')));
                return "url(\"" + proxyUrl + "\")";
            }, RegexOptions.IgnoreCase);
            // O widget retorna o iframe do streamcontrol dentro de HTML/JSON
            // em uma segunda etapa; também é necessário reescrever essa URL.
            var absolutasRewritten = ReescreverUrlsAbsolutasFluig(cssRewritten, proxyBase);

            // Nem toda URL do Fluig existe pronta no HTML/JSON que baixamos: várias telas
            // (ex.: clicar num anexo pra abrir no visualizador) montam a URL via JavaScript
            // SÓ no momento do clique, concatenando host/porta em tempo de execução. Como
            // esse texto nunca aparece pronto na resposta, os reescritas acima (baseadas em
            // regex sobre o HTML/JSON estático) não têm como pegá-la. Por isso injetamos um
            // interceptador no navegador: ele entra em ação sempre que o próprio Fluig tentar
            // abrir/navegar/setar um iframe para o host real do Fluig, e troca a URL na hora
            // por uma relativa (mesma origem do nosso painel), que cai nas rotas de proxy
            // acima — antes do navegador chegar a fazer a requisição para o host errado.
            return InjetarInterceptadorFluig(absolutasRewritten);
        }

        // Instala, o mais cedo possível na página, um "patch" que intercepta qualquer
        // tentativa de o Fluig montar/abrir uma URL absoluta apontando pro host real
        // (window.open, iframe.src, cliques em <a href>, setAttribute) e a reescreve para
        // uma URL relativa antes do navegador buscar. Resolve o caso do PDF/anexo que abre
        // em branco: a tela nativa do Fluig recusa ser embutida num <iframe> de outra origem
        // (X-Frame-Options), e sem essa interceptação o navegador nem chega a passar pela
        // sessão técnica do nosso servidor.
        private static string InjetarInterceptadorFluig(string html)
        {
            string script = "<script id=\"painel-fluig-interceptor\">(function(){" +
                "if(window.__painelFluigInterceptorInstalled)return;window.__painelFluigInterceptorInstalled=true;" +
                "var FLUIG_HOST='" + FLUIG_HOST_JS + "';" +
                "function ehFluig(url){try{var u=new URL(url,window.location.href);return u.hostname===FLUIG_HOST;}catch(e){return false;}}" +
                "function reescrever(url){try{var u=new URL(url,window.location.href);if(u.hostname!==FLUIG_HOST)return url;return u.pathname+u.search+u.hash;}catch(e){return url;}}" +
                "var origOpen=window.open;" +
                "window.open=function(url){var args=Array.prototype.slice.call(arguments);if(typeof url==='string'&&ehFluig(url))args[0]=reescrever(url);return origOpen.apply(window,args);};" +
                "try{" +
                "var srcDesc=Object.getOwnPropertyDescriptor(HTMLIFrameElement.prototype,'src');" +
                "if(srcDesc&&srcDesc.set){Object.defineProperty(HTMLIFrameElement.prototype,'src',{configurable:true,get:srcDesc.get,set:function(v){srcDesc.set.call(this,(typeof v==='string'&&ehFluig(v))?reescrever(v):v);}});}" +
                "}catch(e){}" +
                "var origSetAttribute=Element.prototype.setAttribute;" +
                "Element.prototype.setAttribute=function(name,value){if((name==='src'||name==='href')&&typeof value==='string'&&ehFluig(value))value=reescrever(value);return origSetAttribute.call(this,name,value);};" +
                // O visualizador de documento (fluig-style-guide-player) não usa <iframe src>
                // nem <a href> para buscar o conteúdo do PDF/imagem — ele faz a chamada via
                // fetch()/XMLHttpRequest (AJAX) para montar a visualização em canvas. Sem
                // interceptar essas duas APIs, essa chamada específica escapava de todo o
                // resto do interceptador e ia direto pro host real do Fluig, travando com
                // CORS/X-Frame ou simplesmente falhando em silêncio.
                "if(window.fetch){var origFetch=window.fetch;window.fetch=function(input,init){" +
                "try{" +
                "if(typeof input==='string'&&ehFluig(input)){input=reescrever(input);}" +
                "else if(input&&typeof input==='object'&&input.url&&ehFluig(input.url)){input=new Request(reescrever(input.url),input);}" +
                "}catch(e){}" +
                "return origFetch.call(this,input,init);};}" +
                "var origXhrOpen=XMLHttpRequest.prototype.open;" +
                "XMLHttpRequest.prototype.open=function(method,url){" +
                "var args=Array.prototype.slice.call(arguments);" +
                "try{if(typeof url==='string'&&ehFluig(url))args[1]=reescrever(url);}catch(e){}" +
                "return origXhrOpen.apply(this,args);};" +
                "document.addEventListener('click',function(e){" +
                "var a=e.target&&e.target.closest?e.target.closest('a[href]'):null;" +
                "if(!a||!ehFluig(a.href))return;" +
                "e.preventDefault();e.stopPropagation();" +
                "var destino=reescrever(a.href);" +
                "if(a.target==='_blank')window.open(destino,'_blank');else window.location.href=destino;" +
                "},true);" +
                "})();</script>";

            return Regex.IsMatch(html, "<head\\b[^>]*>", RegexOptions.IgnoreCase)
                ? Regex.Replace(html, "<head\\b[^>]*>", m => m.Value + script, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))
                : script + html;
        }

        private string OcultarShellFluig(string html)
        {
            const string css = "<style id=\"painel-formulario-only\">" +
                ".fs-layout-header,.fs-layout-sidebar,.fluig-header,.responsive-header,.navbar,.navbar-default," +
                ".search-container,.global-search,.user-menu,.profile-menu,#header,#menu,#search,#user-menu," +
                "header[role=\"banner\"],nav[role=\"navigation\"]{display:none!important;}" +
                ".workflowview-new-header small,.workflowview-new-header h2{display:none!important;}" +
                "#ecm_wkfview,.workflowview-new,#tabContent{background:#fff!important;}" +
                ".workflowview-new-header{padding-top:0!important;}" +
                "body{padding:0!important;margin:0!important;}" +
                "</style>";
            var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            var script = "<script>(function(){function openForm(){var a=document.querySelector('#formTab a');if(a)a.click();}if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',openForm);else setTimeout(openForm,0);})();</script>";
            return head >= 0 ? html.Insert(head, css + script) : css + script + html;
        }

        // A tela nativa do Fluig (pageworkflowview) é uma página de portal inteira: cabeçalho,
        // menu lateral, busca etc., com o widget do chamado dentro de um "slot" de layout
        // (ex.: <div id="SlotA">). Em vez de tentar esconder o cabeçalho via CSS — frágil, pois
        // depende de nomes de classe que mudam entre versões/temas do Fluig — localizamos o slot
        // que contém o widget "ecm_workflowview" e extraímos só ele, descartando o resto da página.
        private static string? ExtrairTelaChamado(string html)
        {
            var marcador = Regex.Match(html, "appcode=[\"']ecm_workflowview[\"']", RegexOptions.IgnoreCase);
            if (!marcador.Success) return null;

            var trechoAnterior = html.Substring(0, marcador.Index);
            var aberturasSlot = Regex.Matches(trechoAnterior, "<div\\b[^>]*\\bid=[\"']Slot\\w*[\"'][^>]*>", RegexOptions.IgnoreCase);
            if (aberturasSlot.Count == 0) return null;

            var slotStart = aberturasSlot[^1].Index;
            var bloco = ExtrairBlocoDivBalanceado(html, slotStart);
            if (string.IsNullOrEmpty(bloco)) return null;

            // Scripts que rodam antes do slot no corpo da página costumam configurar variáveis
            // globais (contexto, token, usuário) das quais o widget depende para funcionar.
            // Mantemos esses scripts junto com o slot para não quebrar as chamadas ajax das abas.
            var bodyMatch = Regex.Match(html, "<body\\b[^>]*>", RegexOptions.IgnoreCase);
            var bodyStart = bodyMatch.Success ? bodyMatch.Index + bodyMatch.Length : 0;
            var scriptsAntes = bodyStart < slotStart
                ? string.Concat(Regex.Matches(html, "<script\\b[^>]*>[\\s\\S]*?</script>", RegexOptions.IgnoreCase)
                    .Where(m => m.Index >= bodyStart && m.Index < slotStart)
                    .Select(m => m.Value))
                : "";

            // O Fluig também coloca scripts DEPOIS do slot, no rodapé do corpo da página —
            // normalmente scripts que completam/estendem objetos globais (ex.: WCMAPI) dos
            // quais outros scripts do widget dependem (ex.: socialglobal_pt_BR.js chama
            // WCMAPI.isMobileAppMode()). Sem esses scripts, o clique no anexo lança uma
            // exceção não tratada no meio do tratamento do evento e o restante da cadeia de
            // handlers (inclusive o que abriria o visualizador) nunca chega a rodar — a tela
            // fica em branco sem nenhum log visível de "abertura", como se nada tivesse
            // acontecido.
            var slotEnd = slotStart + bloco.Length;
            var bodyEndMatch = Regex.Match(html, "</body\\s*>", RegexOptions.IgnoreCase);
            var bodyEnd = bodyEndMatch.Success ? bodyEndMatch.Index : html.Length;
            var scriptsDepois = slotEnd < bodyEnd
                ? string.Concat(Regex.Matches(html, "<script\\b[^>]*>[\\s\\S]*?</script>", RegexOptions.IgnoreCase)
                    .Where(m => m.Index >= slotEnd && m.Index < bodyEnd)
                    .Select(m => m.Value))
                : "";

            return scriptsAntes + bloco + scriptsDepois;
        }

        private static string? ExtrairBlocoDivBalanceado(string html, int startIndex)
        {
            var divTagRegex = new Regex("<div\\b[^>]*>|</div>", RegexOptions.IgnoreCase);
            var m = divTagRegex.Match(html, startIndex);
            if (!m.Success) return null;

            var depth = 0;
            while (m.Success)
            {
                depth += m.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
                if (depth == 0)
                    return html.Substring(startIndex, (m.Index + m.Length) - startIndex);
                m = divTagRegex.Match(html, m.Index + m.Length);
            }
            return null;
        }

        private static string MontarPaginaMinima(string paginaOriginal, string conteudo)
        {
            var headMatch = Regex.Match(paginaOriginal, "<head\\b[^>]*>[\\s\\S]*?</head>", RegexOptions.IgnoreCase);
            var head = headMatch.Success ? headMatch.Value : "<head><meta charset=\"utf-8\"></head>";
            // Além do reset básico de margem/fundo, escondemos a navegação embutida que o
            // próprio widget do chamado traz (breadcrumb "Início / Central de Tarefas /
            // Chamados" e o título grande do portal): esses links levam pra páginas do Fluig
            // que não fazem sentido dentro deste visualizador isolado — clicar neles abre uma
            // tela quebrada, sem o restante do portal em volta. Os seletores cobrem tanto
            // classes conhecidas quanto qualquer coisa com "breadcrumb" no nome, já que o
            // Fluig varia a marcação entre versões/temas.
            const string css = "<style id=\"painel-formulario-only\">" +
                "html,body{margin:0!important;padding:0!important;background:#fff!important;height:100%!important;}" +
                ".workflowview-new-header small,.workflowview-new-header h2,.workflowview-new-header .breadcrumb," +
                ".workflowview-new-header ol.breadcrumb,.workflowview-new-header ul.breadcrumb," +
                ".workflowview-new-header nav[aria-label='breadcrumb']," +
                "[class*=\"breadcrumb\"],nav[role=\"navigation\"],header[role=\"banner\"]" +
                "{display:none!important;}" +
                // O PDF/anexo agora é exibido dentro de um <iframe> que o próprio Fluig cria
                // sem definir largura/altura (style=""). Sem essas dimensões, o navegador usa o
                // tamanho padrão de um iframe (bem pequeno, ~300x150px, ancorado no canto do
                // container) em vez de ocupar o espaço em branco disponível no modal. Forçamos
                // aqui o iframe e seus contêineres pais a preencherem 100% do espaço disponível.
                "#ecm-documentview-tabDocView,#ecm-documentviewer,#ecm_player" +
                "{width:100%!important;height:100%!important;}" +
                "#ecm-documentview-docframe" +
                "{width:100%!important;height:100%!important;min-height:600px!important;border:0!important;display:block!important;}" +
                "</style>";
            head = Regex.Replace(head, "</head>", css + "</head>", RegexOptions.IgnoreCase);
            // Vários widgets do Fluig (inclusive o visualizador de documento) calculam a altura
            // disponível olhando pra janela/DOM no momento em que o script roda — e nesse
            // instante o restante da página ainda pode não ter se ajustado ao layout enxuto que
            // montamos aqui (sem o cabeçalho/menu do portal). Resultado: o cálculo "trava" num
            // tamanho pequeno e só se corrige de verdade quando a janela é redimensionada de
            // fato. Disparamos esse evento programaticamente (mais de uma vez, conforme o layout
            // se assenta) pra forçar esses componentes a recalcular contra o tamanho real.
            const string script = "<script>(function(){" +
                "function openForm(){var a=document.querySelector('#formTab a');if(a&&!a.closest('li').classList.contains('active'))a.click();}" +
                "function forcarResize(){try{window.dispatchEvent(new Event('resize'));}catch(e){try{var ev=document.createEvent('Event');ev.initEvent('resize',true,true);window.dispatchEvent(ev);}catch(e2){}}}" +
                "function iniciar(){openForm();forcarResize();setTimeout(forcarResize,150);setTimeout(forcarResize,500);setTimeout(forcarResize,1200);}" +
                "if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',iniciar);else setTimeout(iniciar,0);" +
                "})();</script>";
            return "<!DOCTYPE html><html>" + head + "<body>" + conteudo + script + "</body></html>";
        }

        // A página nativa do Fluig traz o próprio título/favicon do Fluig, o que confunde o
        // técnico ao alternar entre abas (parece que ele está "dentro" do Fluig de verdade).
        // Sobrescrevemos com um título e ícone próprios do visualizador.
        private static string AplicarIdentidadeVisualizador(string html, string codigo)
        {
            var titulo = WebUtility.HtmlEncode($"Visualizador de Chamado nº {codigo}");

            var resultado = Regex.IsMatch(html, "<title\\b[^>]*>[\\s\\S]*?</title>", RegexOptions.IgnoreCase)
                ? Regex.Replace(html, "<title\\b[^>]*>[\\s\\S]*?</title>", $"<title>{titulo}</title>", RegexOptions.IgnoreCase)
                : Regex.Replace(html, "<head\\b[^>]*>", m => m.Value + $"<title>{titulo}</title>", RegexOptions.IgnoreCase);

            // Ícone do próprio painel (wwwroot/favicon.ico), servido diretamente pelo app —
            // não passa pelo proxy do Fluig.
            const string favicon = "<link rel=\"icon\" type=\"image/x-icon\" href=\"/favicon.ico\">";
            resultado = Regex.IsMatch(resultado, "</head>", RegexOptions.IgnoreCase)
                ? Regex.Replace(resultado, "</head>", favicon + "</head>", RegexOptions.IgnoreCase)
                : favicon + resultado;

            return resultado;
        }

        private static bool DeveCachearRecurso(string contentType, string path)
        {
            // Imagens/fotos de perfil de usuário não são "recursos estáticos genéricos" —
            // evitamos cachear qualquer coisa por usuário/colaborador.
            if (path.Contains("/picture", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/profile/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/colleague", StringComparison.OrdinalIgnoreCase))
                return false;

            return contentType.Contains("text/css", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("woff", StringComparison.OrdinalIgnoreCase);
        }

        private string ReescreverUrlsAbsolutasFluig(string content, string proxyBase)
        {
            var pattern = Regex.Escape(URL_BASE) + "(?<path>/[^\\\"'\\s<>]+)";
            return Regex.Replace(content, pattern, match =>
            {
                var value = match.Groups["path"].Value;
                var separator = value.IndexOf('?');
                var path = separator >= 0 ? value[..separator] : value;
                var query = separator >= 0 ? value[(separator + 1)..].Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase) : "";
                return proxyBase + "?path=" + Uri.EscapeDataString(path) + (string.IsNullOrEmpty(query) ? "" : "&query=" + Uri.EscapeDataString(query));
            }, RegexOptions.IgnoreCase);
        }

        [HttpGet]
        public IActionResult RecarregarSessoes()
        {
            foreach (var login in _userSessions.Keys.ToList())
                RemoverSessao(login);
            _cache.Clear();
            _cacheRecursosEstaticos.Clear();
            _fluigIndisponivel = false;
            _ultimaVerificacaoFluig = DateTime.MinValue;
            return RedirectToAction("Index");
        }

        #endregion

        #region Busca AGUARDANDO (Pool)

        private async Task<FluigResponse> BuscarSolicitacoesAguardandoPoolAsync(string fila)
        {
            if (UsuarioPadrao == null)
            {
                return new FluigResponse
                {
                    Success = false,
                    ErrorMessage = "Nenhum usuário configurado"
                };
            }

            try
            {
                var resultado = await BuscarSolicitacoesPoolPorUsuarioAsync(UsuarioPadrao, fila);
                return resultado;
            }
            catch (Exception ex)
            {
                return new FluigResponse
                {
                    Success = false,
                    ErrorMessage = $"Exceção: {ex.Message}"
                };
            }
        }

        private async Task<FluigResponse> BuscarSolicitacoesPoolPorUsuarioAsync(UsuarioConfig usuario, string fila)
        {
            var sessionData = await GetOrCreateSessionAsync(usuario);
            return await BuscarTasksPoolAsync(sessionData, usuario, fila);
        }

        private async Task<FluigResponse> BuscarTasksPoolAsync(UserSessionData sessionData, UsuarioConfig usuario, string fila)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var tasksUrl = $"{URL_BASE}/ecm/api/rest/ecm/centralTasks/getTasks/pool/{sessionData.ColleagueId}" +
                   $"?filter=%7B%22id%22:0,%22name%22:%22%22,%22isActive%22:false,%22isModified%22:false,%22fields%22:%5B%5D,%22searchField%22:%22%22,%22searchValue%22:%22%22,%22order%22:%7B%7D,%22firstValue%22:0,%22pageDirection%22:%22forward%22%7D" +
                   $"&taskId=Pool%3ARole%3A{WebUtility.UrlEncode(fila)}" +
                   $"&offset=0&_search=false&nd={timestamp + 1000}&rows=200&page=1&sidx=startDateProcess&sord=asc&_={timestamp}";

            using var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = sessionData.CookieContainer ?? new CookieContainer(),
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Accept-Language", "pt-BR,pt;q=0.9,en;q=0.8");

            if (!string.IsNullOrEmpty(sessionData.JwtToken))
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {sessionData.JwtToken}");
            }

            try
            {
                var response = await client.GetAsync(tasksUrl);

                if (!response.IsSuccessStatusCode)
                {
                    // Se falhar, tenta recriar a sessão
                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        RemoverSessao(usuario.Login);
                        sessionData = await GetOrCreateSessionAsync(usuario);
                        // Tenta novamente com a nova sessão
                        return await BuscarTasksPoolAsync(sessionData, usuario, fila);
                    }
                    
                    return new FluigResponse
                    {
                        Success = false,
                        ErrorMessage = $"Erro na API Pool. Status: {response.StatusCode}"
                    };
                }

                var json = await response.Content.ReadAsStringAsync();

                // Verifica se o JSON é válido
                if (string.IsNullOrWhiteSpace(json) || !IsValidJson(json))
                {
                    return new FluigResponse
                    {
                        Success = false,
                        ErrorMessage = "Resposta da API inválida (não é JSON)"
                    };
                }

                dynamic? data;
                try
                {
                    data = JsonConvert.DeserializeObject<dynamic>(json);
                    if (data == null) 
                        return new FluigResponse { Success = false, ErrorMessage = "Resposta JSON vazia" };
                }
                catch (JsonReaderException)
                {
                    return new FluigResponse { Success = false, ErrorMessage = "Erro de parsing JSON" };
                }

                var solicitacoes = new List<Solicitacao>();
                if (data?.invdata != null)
                {
                    foreach (var item in data.invdata)
                    {
                        string abertura = "";
                        DateTime? dataAbertura = null;

                        if (item.startDateProcess != null)
                        {
                            try
                            {
                                long timestampMs = (long)item.startDateProcess;
                                var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime.AddHours(-3);
                                abertura = dateTime.ToString("dd/MM/yyyy HH:mm");
                                dataAbertura = dateTime;
                            }
                            catch { abertura = item.startDateProcess?.ToString() ?? ""; }
                        }

                        string prazoTarefa = item.dateExpires?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(prazoTarefa))
                        {
                            try
                            {
                                var prazoLimpo = prazoTarefa.Replace("Até ", "").Replace("Desde ", "");
                                if (DateTime.TryParse(prazoLimpo, out var dataPrazo))
                                    prazoTarefa = dataPrazo.ToString("dd/MM/yyyy HH:mm");
                            }
                            catch { }
                        }

                        solicitacoes.Add(new Solicitacao
                        {
                            Codigo = item.processInstanceId?.ToString() ?? "",
                            Detalhe = item.fieldValue6?.ToString() ?? "",
                            Solicitante = item.requesterName?.ToString() ?? "",
                            Setor = item.fieldValue3?.ToString() ?? "",
                            Abertura = abertura,
                            DataAbertura = dataAbertura,
                            PrazoTarefa = prazoTarefa,
                            Estado = "Aguardando",
                            TipoAtendimento = "Aguardando"
                        });
                    }
                }

                // Usar HashSet para remover duplicatas
                var uniqueSolicitacoes = new HashSet<string>();
                solicitacoes = solicitacoes
                    .Where(s => uniqueSolicitacoes.Add(s.Codigo))
                    .ToList();

                return new FluigResponse
                {
                    Success = true,
                    Solicitacoes = solicitacoes,
                    UsuarioLogado = usuario.Nome
                };
            }
            catch (HttpRequestException)
            {
                // Erro de conexão - Fluig pode estar offline
                return new FluigResponse
                {
                    Success = false,
                    ErrorMessage = "Erro de conexão com o Fluig. Verifique se o servidor está online."
                };
            }
        }

        /// <summary>
        /// Verifica se uma string é um JSON válido
        /// </summary>
        private bool IsValidJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;
                
            json = json.Trim();
            if ((json.StartsWith("{") && json.EndsWith("}")) || 
                (json.StartsWith("[") && json.EndsWith("]")))
            {
                try
                {
                    JsonConvert.DeserializeObject(json);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        #endregion

        #region Busca EM ATENDIMENTO - PARALELIZADA para TODOS os colaboradores

        private async Task<FluigResponse> BuscarProcessosEmAtendimentoUnificadoAsync()
        {
            var todasSolicitacoes = new ConcurrentBag<Solicitacao>();
            var errosPorUsuario = new ConcurrentBag<string>();

            // Executar em PARALELO para todos os usuários
            var tasks = UsuariosConfig.Select(async usuarioConfig =>
            {
                try
                {
                    var sessaoUsuario = await GetOrCreateSessionAsync(usuarioConfig);
                    var resultadoNormal = await BuscarProcessosAtendimentoAsync(sessaoUsuario, usuarioConfig);
                    var nomeResponsavel = await ObterNomeFuncionarioAsync(sessaoUsuario, usuarioConfig.Login, usuarioConfig.Nome);

                    if (resultadoNormal.Success)
                    {
                        foreach (var sol in resultadoNormal.Solicitacoes)
                        {
                            sol.Responsavel = nomeResponsavel;
                            sol.ResponsavelLogin = usuarioConfig.Login;
                            sol.TipoAtendimento = "EmAtendimento";
                            todasSolicitacoes.Add(sol);
                        }
                    }
                    else if (!string.IsNullOrEmpty(resultadoNormal.ErrorMessage))
                    {
                        errosPorUsuario.Add($"{usuarioConfig.Nome}: {resultadoNormal.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    errosPorUsuario.Add($"{usuarioConfig.Nome}: Exceção - {ex.Message}");
                }
            });

            await Task.WhenAll(tasks);

            return new FluigResponse
            {
                Success = true,
                Solicitacoes = todasSolicitacoes.ToList(),
                ErrorMessage = errosPorUsuario.Any() ? string.Join("; ", errosPorUsuario) : null
            };
        }

        private async Task<string> ObterNomeFuncionarioAsync(UserSessionData sessionData, string chapa, string fallback)
        {
            if (_nomesFuncionarios.TryGetValue(chapa, out var nomeExistente) && !string.IsNullOrWhiteSpace(nomeExistente))
                return nomeExistente;
            await _nomesFuncionariosLock.WaitAsync();
            try
            {
                if (_nomesFuncionariosAtualizadoEm < DateTime.UtcNow.AddHours(-6))
                {
                    var login = Environment.GetEnvironmentVariable("LOGIN_FLUIG_VISUALIZADOR");
                    var senha = Environment.GetEnvironmentVariable("SENHA_FLUIG_VISUALIZADOR");
                    UserSessionData sessaoNomes = sessionData;
                    if (!string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(senha))
                        sessaoNomes = await GetOrCreateSessionAsync(new UsuarioConfig { Login = login, Senha = senha, Nome = "Consulta de Funcionários" });
                    var uri = new Uri(URL_BASE + "/api/public/ecm/dataset/search?datasetId=ds_funcionario_dados_adicionais&filterFields=CODFILIAL," + Uri.EscapeDataString("35"));
                    var (resposta, corpo) = await EncaminharFluigAsync(sessaoNomes, uri, "GET", null);
                    if (resposta.IsSuccessStatusCode)
                    {
                        var json = Encoding.UTF8.GetString(corpo);
                        if (IsValidJson(json))
                        {
                            var raiz = JObject.Parse(json);
                            if (raiz["content"] is JArray content)
                            {
                                foreach (var item in content.OfType<JObject>())
                                {
                                    var codigo = item.Properties().FirstOrDefault(x => string.Equals(x.Name, "CHAPA", StringComparison.OrdinalIgnoreCase))?.Value?.ToString()?.Trim();
                                    var nomeFuncionario = item.Properties().FirstOrDefault(x => string.Equals(x.Name, "FUNCIONARIO", StringComparison.OrdinalIgnoreCase))?.Value?.ToString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(codigo) && !string.IsNullOrWhiteSpace(nomeFuncionario) && !string.Equals(nomeFuncionario, "undefined", StringComparison.OrdinalIgnoreCase))
                                        _nomesFuncionarios[codigo] = nomeFuncionario;
                                }
                            }
                            _nomesFuncionariosAtualizadoEm = DateTime.UtcNow;
                        }
                    }
                }
                if (_nomesFuncionarios.TryGetValue(chapa, out var nome) && !string.IsNullOrWhiteSpace(nome))
                    return nome;
            }
            catch { }
            finally { _nomesFuncionariosLock.Release(); }
            return string.IsNullOrWhiteSpace(fallback) ? chapa : fallback;
        }

        private async Task<FluigResponse> BuscarProcessosNormaisAsync(UsuarioConfig usuario)
        {
            var sessionData = await GetOrCreateSessionAsync(usuario);
            return await BuscarProcessosAtendimentoAsync(sessionData, usuario);
        }

        private async Task<FluigResponse> BuscarProcessosAtendimentoAsync(UserSessionData sessionData, UsuarioConfig usuario)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            var filter = new
            {
                id = 0,
                name = "",
                isActive = false,
                isModified = false,
                fields = new[]
                {
                    new { key = "processDescription", value = "chamados", type = "REQUEST", visible = false }
                },
                searchField = "PROCESS_DESCRIPTION",
                searchValue = "chamados",
                order = new { },
                firstValue = 0,
                pageDirection = "forward"
            };
            
            var encodedFilter = Uri.EscapeDataString(JsonConvert.SerializeObject(filter));
            
            var tasksUrl = $"{URL_BASE}/ecm/api/rest/ecm/centralTasks/getTasks/open/{sessionData.ColleagueId}" +
                $"?filter={encodedFilter}" +
                $"&_search=false&nd={timestamp + 1000}&rows=200&page=1&sidx=processInstanceId&sord=asc&_={timestamp}";
            
            using var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = sessionData.CookieContainer ?? new CookieContainer(),
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Accept-Language", "pt-BR,pt;q=0.9,en;q=0.8");
            client.DefaultRequestHeaders.Add("x-requested-with", "XMLHttpRequest");
            
            if (!string.IsNullOrEmpty(sessionData.JwtToken))
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {sessionData.JwtToken}");
            }
            
            try
            {
                var response = await client.GetAsync(tasksUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    // Se falhar, tenta recriar a sessão
                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        RemoverSessao(usuario.Login);
                        sessionData = await GetOrCreateSessionAsync(usuario);
                        return await BuscarProcessosAtendimentoAsync(sessionData, usuario);
                    }
                    
                    return new FluigResponse
                    {
                        Success = false,
                        ErrorMessage = $"Erro na API. Status: {response.StatusCode}"
                    };
                }
                
                var json = await response.Content.ReadAsStringAsync();
                
                // Verifica se o JSON é válido
                if (string.IsNullOrWhiteSpace(json) || !IsValidJson(json))
                {
                    return new FluigResponse
                    {
                        Success = false,
                        ErrorMessage = "Resposta da API inválida (não é JSON)"
                    };
                }
                
                dynamic? data;
                try
                {
                    data = JsonConvert.DeserializeObject<dynamic>(json);
                    if (data == null) 
                        return new FluigResponse { Success = false, ErrorMessage = "Resposta JSON vazia" };
                }
                catch (JsonReaderException)
                {
                    return new FluigResponse { Success = false, ErrorMessage = "Erro de parsing JSON" };
                }
                
                var solicitacoes = new List<Solicitacao>();
                
                // A consulta traz todos os stateDescription. A tela geral filtra os
                // dois estados tradicionais; a tela do usuário logado usa os demais.
                if (data?.invdata != null)
                {
                    foreach (var item in data.invdata)
                    {
                        string estado = item.stateDescription?.ToString() ?? "";
                        
                        string abertura = "";
                        DateTime? dataAbertura = null;
                        
                        if (item.startDateProcess != null)
                        {
                            try
                            {
                                long timestampMs = (long)item.startDateProcess;
                                var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime.AddHours(-3);
                                abertura = dateTime.ToString("dd/MM/yyyy HH:mm");
                                dataAbertura = dateTime;
                            }
                            catch { abertura = item.startDateProcess?.ToString() ?? ""; }
                        }
                        
                        string prazoTarefa = item.dateExpires?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(prazoTarefa))
                        {
                            try
                            {
                                var prazoLimpo = prazoTarefa.Replace("Até ", "").Replace("Desde ", "");
                                if (DateTime.TryParse(prazoLimpo, out var dataPrazo))
                                    prazoTarefa = dataPrazo.ToString("dd/MM/yyyy HH:mm");
                            }
                            catch { }
                        }
                        
                        solicitacoes.Add(new Solicitacao
                        {
                            Codigo = item.processInstanceId?.ToString() ?? "",
                            Detalhe = item.fieldValue6?.ToString() ?? "",
                            Solicitante = item.requesterName?.ToString() ?? "",
                            Setor = item.fieldValue3?.ToString() ?? "",
                            Abertura = abertura,
                            DataAbertura = dataAbertura,
                            PrazoTarefa = prazoTarefa,
                            Estado = estado
                        });
                    }
                }
                
                // Remover duplicatas
                var uniqueSolicitacoes = new HashSet<string>();
                solicitacoes = solicitacoes
                    .Where(s => uniqueSolicitacoes.Add(s.Codigo))
                    .ToList();
                
                return new FluigResponse
                {
                    Success = true,
                    Solicitacoes = solicitacoes,
                    UsuarioLogado = usuario.Nome
                };
            }
            catch (HttpRequestException)
            {
                return new FluigResponse
                {
                    Success = false,
                    ErrorMessage = "Erro de conexão com o Fluig."
                };
            }
        }
        #endregion

        #region Contagem - COM CACHE (Hoje e Ontem)

        private async Task<int> BuscarContagemPorDataComCacheAsync(UsuarioConfig usuario, DateTime data)
        {
            var cacheKey = $"contagem_{usuario.Login}_{data:yyyyMMdd}";
            
            // Tentar obter do cache
            if (_cache.TryGetValue(cacheKey, out var cachedItem) && 
                cachedItem.Expiration > DateTime.Now)
            {
                return cachedItem.Value;
            }
            
            // Buscar normalmente
            var contagem = await BuscarContagemPorDataAsync(usuario, data);
            
            // Cache: 1 hora para dias anteriores, 1 minuto para hoje
            var cacheMinutes = data.Date == DateTime.Now.Date ? 1 : 60;
            _cache[cacheKey] = new CacheItem { Value = contagem, Expiration = DateTime.Now.AddMinutes(cacheMinutes) };
            
            return contagem;
        }

        private async Task<int> BuscarContagemPorDataAsync(UsuarioConfig usuario, DateTime data)
        {
            var sessionData = await GetOrCreateSessionAsync(usuario);

            var dataInicio = data.Date.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var dataFim = data.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            var url = $"{BPM_API_URL}?processId=ChamadosTI" +
                      $"&initialStartDate={Uri.EscapeDataString(dataInicio)}" +
                      $"&finalStartDate={Uri.EscapeDataString(dataFim)}" +
                      $"&expand=requester" +
                      $"&expand=currentMovements" +
                      $"&pageSize=200";

            using var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = sessionData.CookieContainer ?? new CookieContainer(),
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("x-requested-with", "XMLHttpRequest");
            client.DefaultRequestHeaders.Add("Referer", $"{URL_BASE}/portal/");

            try
            {
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    return 0;
                }

                var json = await response.Content.ReadAsStringAsync();
                
                // Verifica se o JSON é válido
                if (!IsValidJson(json))
                {
                    return 0;
                }
                
                dynamic? dataJson = JsonConvert.DeserializeObject(json);

                int contagem = 0;
                if (dataJson?.items != null)
                {
                    string userCode = usuario.UserCode ?? usuario.Login;
                    foreach (var item in dataJson.items)
                    {
                        string status = item.status?.ToString() ?? "";
                        if (status == "CANCELED") continue;

                        string solicitanteCode = item.requester?.code?.ToString() ?? "";
                        if (solicitanteCode == userCode) continue;

                        contagem++;
                    }
                }

                return contagem;
            }
            catch
            {
                return 0;
            }
        }

        #endregion

        #region Gerenciamento de Sessão - OTIMIZADO

        private static void RemoverSessao(string login)
        {
            if (_userSessions.TryRemove(login, out var removida))
                removida.Client?.Dispose();
        }

        private async Task<UserSessionData> GetOrCreateSessionAsync(UsuarioConfig usuario)
        {
            // Verificar se tem sessão válida (menos de 25 minutos)
            if (_userSessions.TryGetValue(usuario.Login, out var sessionData) && 
                sessionData.LastActivity > DateTime.Now.AddMinutes(-25))
            {
                sessionData.LastActivity = DateTime.Now;
                return sessionData;
            }

            // Usar semáforo para evitar múltiplos logins simultâneos
            await _loginSemaphore.WaitAsync();
            try
            {
                // Verificar novamente após adquirir o lock
                if (_userSessions.TryGetValue(usuario.Login, out sessionData) && 
                    sessionData.LastActivity > DateTime.Now.AddMinutes(-25))
                {
                    sessionData.LastActivity = DateTime.Now;
                    return sessionData;
                }

                var (success, errorMessage, jSessionId, jSessionIdSso, colleagueId, cookieContainer, jwtToken) =
                    await FazerLoginCompletoAsync(usuario.Login, usuario.Senha);

                if (!success)
                {
                    throw new Exception($"Falha no login: {errorMessage}");
                }

                sessionData = new UserSessionData
                {
                    JSessionId = jSessionId ?? string.Empty,
                    JSessionIdSso = jSessionIdSso ?? string.Empty,
                    ColleagueId = colleagueId ?? string.Empty,
                    CookieContainer = cookieContainer ?? new CookieContainer(),
                    JwtToken = jwtToken ?? string.Empty,
                    LastActivity = DateTime.Now,
                    NomeUsuario = usuario.Nome
                };
                sessionData.Client = CriarClienteFluig(sessionData.CookieContainer);

                _userSessions.AddOrUpdate(usuario.Login, sessionData, (k, old) =>
                {
                    old.Client?.Dispose();
                    return sessionData;
                });
                return sessionData;
            }
            finally
            {
                _loginSemaphore.Release();
            }
        }

        #endregion

        #region Login Completo

        private async Task<(bool Success, string ErrorMessage, string? JSessionId, string? JSessionIdSso, string? ColleagueId, CookieContainer? CookieContainer, string? JwtToken)>
            FazerLoginCompletoAsync(string usuario, string senha)
        {
            var postData = $"j_username={CodificarParaFormularioWebLatin1(usuario)}&j_password={CodificarParaFormularioWebLatin1(senha)}";
            var content = new StringContent(postData, EncodingLatin1, "application/x-www-form-urlencoded");

            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
            {
                CharSet = "ISO-8859-1"
            };

            var cookieContainer = new CookieContainer();

            using var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = cookieContainer,
                AllowAutoRedirect = true,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Referer", $"{URL_BASE}/portal/");
            client.DefaultRequestHeaders.Add("Origin", URL_BASE);

            try
            {
                var response = await client.PostAsync(LOGIN_URL, content);
                var responseText = await response.Content.ReadAsStringAsync();

                bool temJSessionCookie = cookieContainer.GetCookies(new Uri(URL_BASE))["JSESSIONID"] != null;

                bool pareceErroLogin = (response.RequestMessage?.RequestUri?.ToString().IndexOf("loginerror", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                                     || (responseText?.IndexOf("<title>Login</title>", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                                     || (responseText?.Contains("usuário ou senha inválidos", StringComparison.OrdinalIgnoreCase) ?? false);

                if (temJSessionCookie && !pareceErroLogin)
                {
                    var colleagueId = await ObterColleagueIdComCookieContainer(cookieContainer, usuario);

                    string? jwtToken = null;
                    try
                    {
                        var cookies = cookieContainer.GetCookies(new Uri(URL_BASE));
                        var jwtCookie = cookies["jwt.token"];
                        jwtToken = jwtCookie?.Value;
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(colleagueId))
                    {
                        return (true, string.Empty,
                            cookieContainer.GetCookies(new Uri(URL_BASE))["JSESSIONID"]?.Value,
                            cookieContainer.GetCookies(new Uri(URL_BASE))["JSESSIONIDSSO"]?.Value,
                            colleagueId, cookieContainer, jwtToken);
                    }
                    else
                    {
                        return (false, "Falha ao obter ColleagueId", null, null, null, null, null);
                    }
                }

                var erro = response.StatusCode == HttpStatusCode.OK && (responseText?.Contains("login", StringComparison.OrdinalIgnoreCase) ?? false)
                            ? "Credenciais inválidas"
                            : $"Falha de autenticação. Status: {response.StatusCode}.";

                return (false, erro, null, null, null, null, null);
            }
            catch (Exception ex)
            {
                return (false, $"Exceção: {ex.Message}", null, null, null, null, null);
            }
        }

        private string CodificarParaFormularioWebLatin1(string valor)
        {
            if (string.IsNullOrEmpty(valor)) return string.Empty;

            var bytesLatin1 = EncodingLatin1.GetBytes(valor);
            var resultado = new StringBuilder();

            foreach (byte b in bytesLatin1)
            {
                if (b == 0x20)
                {
                    resultado.Append('+');
                }
                else if ((b >= 0x41 && b <= 0x5A) ||
                         (b >= 0x61 && b <= 0x7A) ||
                         (b >= 0x30 && b <= 0x39) ||
                         b == 0x2D || b == 0x5F || b == 0x2E || b == 0x7E)
                {
                    resultado.Append((char)b);
                }
                else
                {
                    resultado.Append('%');
                    resultado.Append(b.ToString("X2"));
                }
            }

            return resultado.ToString();
        }

        private async Task<string?> ObterColleagueIdComCookieContainer(CookieContainer cookieContainer, string username)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    UseCookies = true,
                    CookieContainer = cookieContainer ?? new CookieContainer(),
                    ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
                };

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                client.DefaultRequestHeaders.Add("Accept", "application/json");

                var url = $"{URL_BASE}/ecm/api/rest/ecm/colleague/getColleagueByLogin/?username={WebUtility.UrlEncode(username)}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                
                if (!IsValidJson(json)) return null;

                try
                {
                    dynamic? data = JsonConvert.DeserializeObject(json);
                    string? colleagueId = data?.content?.colleaguePK?.colleagueId?.ToString();
                    if (!string.IsNullOrEmpty(colleagueId)) return colleagueId;
                }
                catch
                {
                    var match = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"colleagueId\"\\s*:\\s*\"([^\"]+)\"");
                    if (match.Success) return match.Groups[1].Value;
                }
            }
            catch { }

            return null;
        }

        #endregion
    }

    // Classe para cache
    public class CacheItem
    {
        public int Value { get; set; }
        public DateTime Expiration { get; set; }
    }

    public class RecursoCacheado
    {
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = "application/octet-stream";
        public DateTime Expira { get; set; }
    }
}

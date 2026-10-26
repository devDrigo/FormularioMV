using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FluigPainel.Models;

namespace FluigPainel.Services;

/// <summary>
/// Executor de automações no servidor. O Apps Script apenas notifica e faz a
/// ponte de leitura/escrita da planilha; a decisão e a abertura ficam aqui.
/// </summary>
public sealed class AutomaticCallScheduler : BackgroundService
{
    private readonly AutomaticCallStoreService _store;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AutomaticCallScheduler> _logger;
    private readonly TimeZoneInfo _tz;
    private readonly SemaphoreSlim _ciclo = new(1, 1);
    private readonly SemaphoreSlim _usuarioLock = new(1, 1);
    private readonly Channel<(string Tipo, JsonElement Evento)> _filaNotificacoes = Channel.CreateUnbounded<(string, JsonElement)>();
    private DadosUsuarioFluig? _usuarioCache;
    private DateTime _usuarioCacheExpiraUtc;
    private DateTime _ultimaSincronizacaoUtc = DateTime.MinValue;
    private bool _sincronizacaoInicialConcluida;

    public AutomaticCallScheduler(AutomaticCallStoreService store, IHttpClientFactory http, ILogger<AutomaticCallScheduler> logger)
    {
        _store = store; _http = http; _logger = logger;
        try { _tz = TimeZoneInfo.FindSystemTimeZoneById(Environment.GetEnvironmentVariable("TIME_ZONE_ID") ?? "America/Sao_Paulo"); }
        catch { _tz = TimeZoneInfo.Utc; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        try
        {
            // A planilha tem prioridade em toda inicialização/reinício.
            await SincronizarPlanilhaAsync(stoppingToken, true);
            await ProcessarNotificacaoAsync("inicio", null, stoppingToken);
        }
        catch (Exception ex) { _logger.LogError(ex, "Falha na sincronização inicial das automações"); }
        // Os eventos recebidos pelo endpoint entram nesta fila. O endpoint responde
        // imediatamente ao Apps Script; a abertura continua sendo feita no C#.
        await foreach (var item in _filaNotificacoes.Reader.ReadAllAsync(stoppingToken))
        {
            try { await ProcessarNotificacaoAsync(item.Tipo, item.Evento, stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Falha ao processar evento enfileirado {Tipo}", item.Tipo); }
        }
    }

    public async Task<bool> SincronizarPlanilhaAgoraAsync(CancellationToken ct = default)
    {
        try { return await SincronizarPlanilhaAsync(ct, true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Falha ao sincronizar a planilha"); return false; }
    }

    public bool EnfileirarNotificacao(string tipo, JsonElement evento)
    {
        return _filaNotificacoes.Writer.TryWrite((tipo, evento.Clone()));
    }

    public async Task<object> ProcessarNotificacaoAsync(string tipo, JsonElement? evento, CancellationToken ct = default)
    {
        if (!await _ciclo.WaitAsync(TimeSpan.FromSeconds(30), ct)) return new { success = true, ignorado = true, motivo = "execucao_em_andamento" };
        try
        {
            await SincronizarPlanilhaAsync(ct, !_sincronizacaoInicialConcluida && !string.Equals(tipo, "gatilho_horario", StringComparison.OrdinalIgnoreCase));
            var store = _store.Read();
            var agora = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);
            var executadas = 0; var falhas = 0; var avaliadas = 0; var ignoradasHorario = 0;
            var testeManual = string.Equals(tipo, "execucao_manual", StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation("Notificação de automação {Tipo}: {Total} configurações, horário local {Agora:yyyy-MM-dd HH:mm:ss}", tipo, store.Configuracoes.Count, agora);
            foreach (var config in store.Configuracoes)
            {
                if (!config.Ativo) { _logger.LogInformation("Automação {Nome} ignorada: inativa", config.Nome); continue; }
                var ehHorario = EhModoHorario(config.ModoGatilho);
                if (!testeManual && ehHorario && !HorarioDevido(config, agora))
                {
                    ignoradasHorario++; _logger.LogInformation("Automação {Nome} ignorada: horário ainda não devido. Modo={Modo}, Periodicidade={Periodicidade}, Horários={Horarios}", config.Nome, config.ModoGatilho, config.Periodicidade, string.Join(",", config.Horarios ?? new())); continue;
                }
                avaliadas++;
                try { executadas += await ProcessarConfiguracaoAsync(config, evento, ct); }
                catch (Exception ex) { falhas++; _logger.LogError(ex, "Falha na automação {Nome}", config.Nome); }
            }
            return new { success = true, tipo, automacoes = executadas, avaliadas, ignoradasHorario, falhas, horarioServidor = agora.ToString("yyyy-MM-dd HH:mm:ss"), executadoEm = DateTime.UtcNow };
        }
        finally { _ciclo.Release(); }
    }

    private async Task<bool> SincronizarPlanilhaAsync(CancellationToken ct, bool forcar = false)
    {
        if (!forcar && DateTime.UtcNow - _ultimaSincronizacaoUtc < TimeSpan.FromSeconds(20)) return true;
        var url = Environment.GetEnvironmentVariable("PLANILHA_SYNC_WEBAPP_URL");
        if (string.IsNullOrWhiteSpace(url)) return false;
        var token = Environment.GetEnvironmentVariable("PLANILHA_SYNC_TOKEN") ?? Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY") ?? "";
        var endpoint = url + (url.Contains('?') ? "&" : "?") + "acao=sincronizar&token=" + Uri.EscapeDataString(token);
        var resposta = await _http.CreateClient().GetAsync(endpoint, ct);
        var texto = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode) throw new InvalidOperationException($"Sincronização HTTP {(int)resposta.StatusCode}: {texto[..Math.Min(300, texto.Length)]}");
        using var doc = JsonDocument.Parse(texto); var raiz = doc.RootElement;
        if (!raiz.TryGetProperty("success", out var ok) || !ok.GetBoolean()) throw new InvalidOperationException("A planilha recusou a sincronização.");
        _ultimaSincronizacaoUtc = DateTime.UtcNow; _sincronizacaoInicialConcluida = true;
        if (raiz.TryGetProperty("store", out var rawStore) && rawStore.ValueKind == JsonValueKind.Object)
        {
            var recebido = JsonSerializer.Deserialize<AutomaticCallStore>(rawStore.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (recebido != null)
            {
                recebido.Configuracoes ??= new(); recebido.Regras ??= new(); recebido.Fontes ??= new(); recebido.Historico ??= new(); recebido.Exclusoes ??= new();
            }
            if (recebido != null && (recebido.Configuracoes.Count > 0 || recebido.Regras.Count > 0 || recebido.Fontes.Count > 0))
            {
                var atual = _store.Read();
                recebido.Historico = (recebido.Historico ?? new()).Concat(atual.Historico ?? new()).GroupBy(x => x.Id).Select(x => x.First()).OrderByDescending(x => x.AbertoEm).Take(500).ToList();
                _store.Save(recebido);
            }
        }
        return true;
    }

    private static bool EhModoHorario(string? modo)
    {
        var valor = (modo ?? "").Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        valor = new string(valor.Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        return valor == "horario" || valor == "horario programado";
    }

    private static bool HorarioDevido(AutomaticCallConfig c, DateTime agora)
    {
        if (!DataValida(c, agora)) return false;
        foreach (var horario in c.Horarios ?? new())
        {
            if (!TimeSpan.TryParse((horario ?? "").Trim(), CultureInfo.InvariantCulture, out var alvo)) continue;
            // Permite até 5 minutos adiantado e qualquer atraso do mesmo período diário.
            var diferenca = (agora.TimeOfDay - alvo).TotalMinutes;
            if (diferenca >= -5 && diferenca <= 24 * 60) return true;
        }
        return false;
    }

    private static bool DataValida(AutomaticCallConfig c, DateTime agora)
    {
        var p = c.Periodicidade ?? "Diariamente";
        if (p == "Semanalmente" && !string.IsNullOrWhiteSpace(c.DiaSemana) && !string.Equals(c.DiaSemana, agora.ToString("dddd", new CultureInfo("pt-BR")), StringComparison.OrdinalIgnoreCase)) return false;
        if ((p == "Mensalmente" || p == "Trimestralmente" || p == "Semestralmente" || p == "Anualmente") && c.DiaMes != agora.Day) return false;
        if (p == "Anualmente" && c.MesAno != agora.Month) return false;
        if (p == "Trimestralmente" && ((agora.Month - c.MesAno + 12) % 3) != 0) return false;
        if (p == "Semestralmente" && ((agora.Month - c.MesAno + 12) % 6) != 0) return false;
        return true;
    }

    private async Task<int> ProcessarConfiguracaoAsync(AutomaticCallConfig c, JsonElement? evento, CancellationToken ct)
    {
        var regra = _store.Read().Regras.FirstOrDefault(x => x.Id == c.RegraId);
        var condicoes = regra?.Condicoes ?? new();
        // Automação de horário simples: não depende de aba nem de regra.
        if (condicoes.Count == 0)
        {
            var numeroFixo = await AbrirAsync(c, c.Titulo ?? c.Nome, c.Descricao ?? c.Nome, ct);
            return string.IsNullOrWhiteSpace(numeroFixo) ? 0 : 1;
        }
        var aba = c.AbaFonte; if (string.IsNullOrWhiteSpace(aba)) return 0;
        var dados = await LerAbaAsync(aba, ct); if (dados == null) return 0;
        var indices = dados.Cabecalhos.Select((x, i) => new { Nome = x.Trim(), I = i }).GroupBy(x => x.Nome, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().I, StringComparer.OrdinalIgnoreCase);
        var colunaRetorno = EncontrarColuna(indices, c.ColunaNumeroChamado); if (!string.IsNullOrWhiteSpace(c.ColunaNumeroChamado) && colunaRetorno < 0) return 0;
        var inicio = dados.PrimeiraLinhaDados; var quantidade = 0;
        for (var i = 0; i < dados.Linhas.Count; i++)
        {
            var linha = dados.Linhas[i];
            if (!EventoAfetaLinha(evento, aba, inicio + i) && evento.HasValue && evento.Value.ValueKind == JsonValueKind.Object && evento.Value.TryGetProperty("tipo", out var t) && t.GetString() == "edicao") continue;
            if (colunaRetorno >= 0 && (colunaRetorno >= linha.Count || !string.IsNullOrWhiteSpace(linha[colunaRetorno]))) continue;
            if (!Avaliar(condicoes, regra?.Combinacao ?? "E", linha, indices)) continue;
            var titulo = Substituir(c.Titulo, dados.Cabecalhos, linha); var descricao = Substituir(c.Descricao, dados.Cabecalhos, linha);
            var numero = await AbrirAsync(c, titulo, descricao, ct); if (string.IsNullOrWhiteSpace(numero)) continue;
            if (colunaRetorno >= 0) await GravarChamadoAsync(aba, inicio + i, colunaRetorno + 1, numero, ct); quantidade++;
        }
        return quantidade;
    }

    private static int EncontrarColuna(Dictionary<string, int> indices, string nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return -1;
        if (indices.TryGetValue(nome.Trim(), out var i)) return i;
        if (int.TryParse(nome.Trim(), out var n)) return n - 1;
        return IndiceColunaExcel(nome);
    }
    private static int IndiceColunaExcel(string referencia)
    {
        var texto = referencia.Trim().ToUpperInvariant(); if (texto.Length == 0) return -1;
        var resultado = 0; foreach (var caractere in texto) { if (caractere < 'A' || caractere > 'Z') return -1; resultado = resultado * 26 + caractere - 'A' + 1; }
        return resultado - 1;
    }
    private static bool EventoAfetaLinha(JsonElement? e, string aba, int linha)
    { if (!e.HasValue || e.Value.ValueKind != JsonValueKind.Object) return true; if (e.Value.TryGetProperty("aba", out var a) && !string.Equals(a.GetString(), aba, StringComparison.OrdinalIgnoreCase)) return false; if (e.Value.TryGetProperty("linhaInicial", out var l) && e.Value.TryGetProperty("quantidadeLinhas", out var q)) return linha >= l.GetInt32() && linha < l.GetInt32() + q.GetInt32(); return true; }
    private static bool Avaliar(List<AutomaticTriggerCondition> cs, string comb, List<string> row, Dictionary<string, int> idx) => (string.Equals(comb, "OU", StringComparison.OrdinalIgnoreCase) ? cs.Any(c => AvaliarUma(c, row, idx)) : cs.All(c => AvaliarUma(c, row, idx)));
    private static bool AvaliarUma(AutomaticTriggerCondition c, List<string> row, Dictionary<string, int> idx)
    { var i = EncontrarColuna(idx, c.Coluna); var atual = i >= 0 && i < row.Count ? row[i].Trim() : ""; var esperado = c.Valor?.Trim() ?? ""; if (c.Operador is "Nulo" or "Está vazio") return atual == ""; if (c.TipoValor == "Número" && double.TryParse(atual.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) && double.TryParse(esperado.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var ne)) return c.Operador == "Menor que" ? n < ne : c.Operador == "Diferente de" ? n != ne : n == ne; if (c.TipoValor == "Data" || c.TipoValor == "Período") { if (!DateTime.TryParse(atual, new CultureInfo("pt-BR"), DateTimeStyles.None, out var d)) return false; var alvo = DateTime.Today; if (c.TipoValor == "Período") alvo = c.PeriodoUnidade.StartsWith("dia", StringComparison.OrdinalIgnoreCase) ? alvo.AddDays(-Math.Max(1, c.PeriodoQuantidade)) : alvo.AddMonths(-Math.Max(1, c.PeriodoQuantidade)); else if (c.ModoData?.Equals("Ontem", StringComparison.OrdinalIgnoreCase) == true) alvo = alvo.AddDays(-1); return c.Operador == "Menor que" ? d.Date < alvo.Date : c.Operador == "Diferente de" ? d.Date != alvo.Date : d.Date == alvo.Date; } return c.Operador == "Menor que" ? string.Compare(atual, esperado, true, CultureInfo.InvariantCulture) < 0 : c.Operador == "Diferente de" ? !string.Equals(atual, esperado, StringComparison.OrdinalIgnoreCase) : string.Equals(atual, esperado, StringComparison.OrdinalIgnoreCase); }
    private static string Substituir(string? texto, List<string> heads, List<string> row)
    {
        return Regex.Replace(texto ?? "", @"\$([A-Za-z_][A-Za-z0-9_]*)", match =>
        {
            var referencia = match.Groups[1].Value;
            var indice = EncontrarColuna(heads.Select((h, i) => new { h, i }).GroupBy(x => x.h.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().i, StringComparer.OrdinalIgnoreCase), referencia);
            return indice >= 0 && indice < row.Count ? row[indice] ?? "" : match.Value;
        });
    }

    private async Task<AbaResposta?> LerAbaAsync(string aba, CancellationToken ct)
    { var baseUrl = Environment.GetEnvironmentVariable("PLANILHA_SYNC_WEBAPP_URL"); if (string.IsNullOrWhiteSpace(baseUrl)) return null; var token = Environment.GetEnvironmentVariable("PLANILHA_SYNC_TOKEN") ?? Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY") ?? ""; var url = baseUrl + (baseUrl.Contains('?') ? "&" : "?") + "acao=ler_aba&aba=" + Uri.EscapeDataString(aba) + "&token=" + Uri.EscapeDataString(token); var resp = await _http.CreateClient().GetAsync(url, ct); resp.EnsureSuccessStatusCode(); return JsonSerializer.Deserialize<AbaResposta>(await resp.Content.ReadAsStringAsync(ct), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
    private async Task GravarChamadoAsync(string aba, int linha, int coluna, string numero, CancellationToken ct)
    { var baseUrl = Environment.GetEnvironmentVariable("PLANILHA_SYNC_WEBAPP_URL"); if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("PLANILHA_SYNC_WEBAPP_URL não configurada"); var token = Environment.GetEnvironmentVariable("PLANILHA_SYNC_TOKEN") ?? Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY") ?? ""; var url = baseUrl + (baseUrl.Contains('?') ? "&" : "?") + "acao=gravar_chamado&token=" + Uri.EscapeDataString(token); var resp = await _http.CreateClient().PostAsJsonAsync(url, new { aba, linha, coluna, numeroChamado = numero }, ct); resp.EnsureSuccessStatusCode(); }

    private static string LoginChamado => Environment.GetEnvironmentVariable("LOGIN_AUTO_CHAMADO") ?? "";
    private static string SenhaChamado => Environment.GetEnvironmentVariable("SENHA_AUTO_CHAMADO") ?? "";

    private sealed class DadosUsuarioFluig { public string TaskUserId { get; set; } = ""; public string UsuarioSolicitante { get; set; } = ""; public string NomeSolicitante { get; set; } = ""; }
    private async Task<DadosUsuarioFluig> ObterDadosUsuarioAsync(CancellationToken ct)
    {
        if (_usuarioCache != null && _usuarioCacheExpiraUtc > DateTime.UtcNow) return _usuarioCache;
        await _usuarioLock.WaitAsync(ct);
        try
        {
            if (_usuarioCache != null && _usuarioCacheExpiraUtc > DateTime.UtcNow) return _usuarioCache;
            var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
            var baseUrl = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL") ?? $"http://127.0.0.1:{port}";
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/Home/DadosUsuarioFluigAutomatico");
            var key = Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY") ?? Environment.GetEnvironmentVariable("PLANILHA_SYNC_TOKEN") ?? "";
            if (!string.IsNullOrWhiteSpace(key)) req.Headers.Add("X-Api-Key", key);
            var resp = await _http.CreateClient().SendAsync(req, ct); resp.EnsureSuccessStatusCode();
            var dados = JsonSerializer.Deserialize<DadosUsuarioFluig>(await resp.Content.ReadAsStringAsync(ct), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Resposta vazia ao obter usuário do Fluig.");
            if (string.IsNullOrWhiteSpace(dados.TaskUserId) || string.IsNullOrWhiteSpace(dados.NomeSolicitante)) throw new InvalidOperationException("Dados incompletos do usuário do Fluig.");
            _usuarioCache = dados; _usuarioCacheExpiraUtc = DateTime.UtcNow.AddHours(12); return dados;
        }
        finally { _usuarioLock.Release(); }
    }

    private async Task<string> AbrirAsync(AutomaticCallConfig c, string titulo, string descricao, CancellationToken ct)
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
        var baseUrl = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL") ?? $"http://127.0.0.1:{port}";
        var key = Environment.GetEnvironmentVariable("FUNCIONARIOS_API_KEY") ?? "";
        var agora = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);
        var prazo = agora.AddHours(c.PrazoHoras > 0 ? c.PrazoHoras : 24);
        var dataHoje = agora.ToString("dd/MM/yyyy");
        var pool = Environment.GetEnvironmentVariable("FLUIG_POOL_RESPONSAVEL") ?? "Pool:Role:TI35";
        var unidade = Environment.GetEnvironmentVariable("FLUIG_UNIDADE") ?? "35";
        var ramal = Environment.GetEnvironmentVariable("FLUIG_RAMAL") ?? "2922";
        var setor = Environment.GetEnvironmentVariable("FLUIG_SETOR") ?? "CSTI - Central de Sistema de Tecnologia da Informação";
        var processo = Environment.GetEnvironmentVariable("FLUIG_PROCESSO_ID") ?? "ChamadosTI";
        var versao = int.TryParse(Environment.GetEnvironmentVariable("FLUIG_PROCESSO_VERSAO"), out var versaoConfigurada) ? versaoConfigurada : 97;
        var usuario = await ObterDadosUsuarioAsync(ct);
        var formData = new object[]
        {
            new { name = "setorinformado", value = "" }, new { name = "papelresponsavel", value = pool },
            new { name = "usuarioSolicitante", value = usuario.UsuarioSolicitante }, new { name = "statusChamado", value = "" },
            new { name = "prazoConclusao", value = prazo.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalMilliseconds }, new { name = "permissaoUsuario", value = "undefined" },
            new { name = "NSolic_11", value = "" }, new { name = "DataSolic_9", value = dataHoje },
            new { name = "Solicitante_12", value = usuario.NomeSolicitante }, new { name = "UnidadeGerenciada", value = unidade },
            new { name = "SETOR", value = setor }, new { name = "Ramal_63", value = ramal },
            new { name = "UnidadeSolicitante", value = unidade }, new { name = "TipoSolic", value = "TI" },
            new { name = "Categoria_22", value = c.Categoria ?? "" }, new { name = "Subcategoria", value = c.Subcategoria ?? "" },
            new { name = "Previsao", value = dataHoje }, new { name = "PrevisaoConclusao", value = prazo.ToString("dd/MM/yyyy HH:mm") },
            new { name = "Imprimir", value = "" }, new { name = "Titulo_54", value = titulo }, new { name = "DescriodoChamado_53", value = descricao },
            new { name = "recategAtend", value = "Nao" }, new { name = "re_categoria", value = c.Categoria ?? "" },
            new { name = "slt_re_categoria", value = "" }, new { name = "re_subcategoria", value = c.Subcategoria ?? "" },
            new { name = "DataHoraAtend", value = "" }, new { name = "TempodeAtendimento", value = "" }, new { name = "Classificacao", value = "" },
            new { name = "ResponsavelChamado2", value = "" }, new { name = "DescriodaSoluo_62", value = "" }, new { name = "movimentarChamado", value = "" },
            new { name = "materiaisutilizados", value = "" }, new { name = "GrupoProduto", value = "" }, new { name = "Produto2", value = "" },
            new { name = "UnidadeMedida", value = "" }, new { name = "Quantidade", value = "" }, new { name = "ValorUnitario", value = "" },
            new { name = "OK", value = "Sim" }, new { name = "Avaliacao", value = "Excelente" }, new { name = "ObservaesGerais_35", value = "" }
        };
        var payload = new
        {
            processInstanceId = 0, processId = processo, version = versao, currentMovto = 0,
            currentState = 4, selectedState = 19, completeTask = true, conditionAfterAutomatic = -1,
            selectedDestinyAfterAutomatic = -1, taskUserId = usuario.TaskUserId, managerMode = false, formData,
            appointments = Array.Empty<object>(), attachments = Array.Empty<object>(), comments = "", internalFields = Array.Empty<object>(), newObservations = Array.Empty<object>(),
            digitalSignature = false, isDigitalSigned = false
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/Home/AbrirChamadoAutomatico?configId=" + Uri.EscapeDataString(c.Id) + "&configNome=" + Uri.EscapeDataString(c.Nome))
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("X-Api-Key", key);
        var response = await _http.CreateClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var documento = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (documento.RootElement.TryGetProperty("numeroChamado", out var numero)) return numero.ToString();
        return "";
    }

    private sealed class AbaResposta { public bool Success { get; set; } public int PrimeiraLinhaDados { get; set; } public List<string> Cabecalhos { get; set; } = new(); public List<List<string>> Linhas { get; set; } = new(); }
}

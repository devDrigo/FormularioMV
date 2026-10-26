using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluigPainel.Models;

namespace FluigPainel.Services
{
    public class AutomaticCallStoreService
    {
        private readonly object _lock = new();
        private readonly string _filePath;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new HorariosConverter() } };

        public AutomaticCallStoreService()
        {
            var dataDirectory = Environment.GetEnvironmentVariable("AUTOMATIC_CALLS_DATA_DIR");
            if (string.IsNullOrWhiteSpace(dataDirectory)) dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            _filePath = Path.Combine(dataDirectory, "automatic-calls.json");
        }

        public AutomaticCallStore Read()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath)) return CriarPadrao();
                try
                {
                    var store = JsonSerializer.Deserialize<AutomaticCallStore>(File.ReadAllText(_filePath), JsonOptions) ?? CriarPadrao();
                    store.Configuracoes ??= new(); store.Fontes ??= new(); store.Regras ??= new(); store.Historico ??= new(); store.Exclusoes ??= new();
                    foreach (var fonte in store.Fontes) { fonte.Abas ??= new(); if (fonte.Abas.Count == 0 && !string.IsNullOrWhiteSpace(fonte.AbaOuTabela)) fonte.Abas.Add(fonte.AbaOuTabela); }
                    foreach (var regra in store.Regras) { if (string.IsNullOrWhiteSpace(regra.FonteId)) regra.FonteId = store.Fontes.FirstOrDefault()?.Id ?? "fonte-planilha-principal"; if (string.IsNullOrWhiteSpace(regra.AbaFonte)) regra.AbaFonte = store.Fontes.FirstOrDefault()?.Abas.FirstOrDefault() ?? ""; }
                    foreach (var config in store.Configuracoes) { config.Horarios ??= new(); if (string.IsNullOrWhiteSpace(config.ColunaNumeroChamadoFonteId)) config.ColunaNumeroChamadoFonteId = config.FonteId; if (string.IsNullOrWhiteSpace(config.ColunaNumeroChamadoAba)) config.ColunaNumeroChamadoAba = config.AbaFonte; }
                    if (!store.Fontes.Any()) store.Fontes.Add(new AutomaticCallSource { Id = "fonte-planilha-principal", Nome = "Funcionários - planilha principal", Tipo = "Google Sheets", Endereco = "Endpoint de funcionários / planilha do Google Sheets", AbaOuTabela = "Funcionários" });
                    if (!store.Regras.Any()) store.Regras.Add(new AutomaticTriggerRule { Id = "regra-desligamento", FonteId = "fonte-planilha-principal", Nome = "Desligamento vencido sem chamado", Descricao = "Identifica funcionários desligados que ainda não possuem chamado.", Condicao = "DEMISSAO anterior a hoje e CHAMADO_DESLIGAMENTO vazio." });
                    foreach (var config in store.Configuracoes.Where(x => x.Id == "desligamento-funcionario")) { config.FonteId = string.IsNullOrWhiteSpace(config.FonteId) ? "fonte-funcionarios" : config.FonteId; config.RegraId = string.IsNullOrWhiteSpace(config.RegraId) ? "regra-desligamento" : config.RegraId; }
                    foreach (var config in store.Configuracoes.Where(x => x.Id == "desligamento-funcionario")) {
                        config.FonteId = string.IsNullOrWhiteSpace(config.FonteId) || config.FonteId == "fonte-funcionarios" ? "fonte-planilha-principal" : config.FonteId;
                        if (string.IsNullOrWhiteSpace(config.Titulo) || config.Titulo == "DESLIGAMENTO DE FUNCIONÁRIO") config.Titulo = "DESLIGAMENTO DE FUNCIONÁRIO - $FUNCIONARIO - Chapa $CHAPA";
                        if (string.IsNullOrWhiteSpace(config.Descricao) || config.Descricao.StartsWith("Criar chamado de desligamento")) config.Descricao = "Solicito realizar o desligamento do usuário no sistema.\n\nFuncionário: $FUNCIONARIO\nChapa: $CHAPA\nCPF: $CPF\nFunção: $FUNCAO\nSetor: $SETOR\nData de demissão: $DEMISSAO\n\nAs informações acima foram obtidas da aba configurada na planilha.";
                    }
                    // Migração: preenche a coluna de gravação do número do chamado em
                    // configurações antigas que ainda não tinham esse campo explícito,
                    // inferindo a partir da condição da regra associada (best-effort).
                    foreach (var config in store.Configuracoes.Where(x => string.IsNullOrWhiteSpace(x.ColunaNumeroChamado)))
                    {
                        var regraAssociada = store.Regras.FirstOrDefault(x => x.Id == config.RegraId);
                        var pista = regraAssociada?.Condicoes?.FirstOrDefault(c => c.Coluna.Contains("CHAMADO", StringComparison.OrdinalIgnoreCase));
                        config.ColunaNumeroChamado = pista?.Coluna ?? (config.Id == "desligamento-funcionario" ? "CHAMADO_DESLIGAMENTO" : "");
                    }
                    // Mantém a fonte/aba do agendamento sempre sincronizada com a
                    // regra associada — evita configuração duplicada e divergente.
                    foreach (var config in store.Configuracoes)
                    {
                        var regraAssociada = store.Regras.FirstOrDefault(x => x.Id == config.RegraId);
                        if (regraAssociada == null) continue;
                        config.FonteId = regraAssociada.FonteId;
                        config.AbaFonte = regraAssociada.AbaFonte;
                    }
                    Save(store);
                    return store;
                }
                catch { return CriarPadrao(); }
            }
        }

        public void Save(AutomaticCallStore store)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var temporary = _filePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(store, JsonOptions));
                File.Move(temporary, _filePath, true);
            }
        }

        public AutomaticCallStore CriarPadrao()
        {
            var store = new AutomaticCallStore();
            var fonte = new AutomaticCallSource { Id = "fonte-planilha-principal", Nome = "Funcionários - planilha principal", Tipo = "Google Sheets", Endereco = "Endpoint de funcionários / planilha do Google Sheets", AbaOuTabela = "Funcionários", Abas = new() { "Funcionários" } };
            var regra = new AutomaticTriggerRule
            {
                Id = "regra-desligamento",
                FonteId = fonte.Id,
                AbaFonte = "Funcionários",
                Nome = "Desligamento vencido sem chamado",
                Descricao = "Identifica funcionários desligados que ainda não possuem chamado.",
                Condicao = "DEMISSAO anterior a hoje e CHAMADO_DESLIGAMENTO vazio. Se houver mais de 5 pendências, avisar para verificar a planilha e listar os funcionários sem data de desligamento.",
                Condicoes = new() { new AutomaticTriggerCondition { Coluna = "DEMISSAO", Operador = "Anterior a hoje", Valor = "" }, new AutomaticTriggerCondition { Coluna = "CHAMADO_DESLIGAMENTO", Operador = "Está vazio", Valor = "" } }
            };
            store.Fontes.Add(fonte); store.Regras.Add(regra);
            store.Configuracoes.Add(new AutomaticCallConfig
            {
                Id = "desligamento-funcionario", Nome = "Desligamento de funcionário", Tipo = "DESLIGAMENTO_FUNCIONARIO", ModoGatilho = "Evento",
                FonteId = fonte.Id, AbaFonte = regra.AbaFonte, RegraId = regra.Id, Periodicidade = "", Horarios = new() { "06:00" },
                ColunaNumeroChamado = "CHAMADO_DESLIGAMENTO",
                Categoria = "", Subcategoria = "", PrazoHoras = 24,
                Titulo = "DESLIGAMENTO DE FUNCIONÁRIO - $FUNCIONARIO - Chapa $CHAPA", Descricao = "Solicito realizar o desligamento do usuário no sistema.\n\nFuncionário: $FUNCIONARIO\nChapa: $CHAPA\nCPF: $CPF\nFunção: $FUNCAO\nSetor: $SETOR\nData de demissão: $DEMISSAO\n\nAs informações acima foram obtidas da aba configurada na planilha."
            });
            Save(store);
            return store;
        }

        public AutomaticCallStore ReadAndCleanHistory()
        {
            var store = Read();
            var limite = DateTime.Now.AddDays(-30);
            var historico = store.Historico.Where(x => x.AbertoEm >= limite).OrderByDescending(x => x.AbertoEm).ToList();
            if (historico.Count != store.Historico.Count) { store.Historico = historico; Save(store); }
            return store;
        }
    }

    internal sealed class HorariosConverter : JsonConverter<System.Collections.Generic.List<string>>
    {
        public override System.Collections.Generic.List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return (reader.GetString() ?? "06:00").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var valores = new System.Collections.Generic.List<string>();
            if (reader.TokenType == JsonTokenType.StartArray) while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) if (reader.TokenType == JsonTokenType.String) valores.Add(reader.GetString() ?? "");
            return valores;
        }
        public override void Write(Utf8JsonWriter writer, System.Collections.Generic.List<string> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray(); foreach (var horario in value ?? new()) writer.WriteStringValue(horario); writer.WriteEndArray();
        }
    }
}

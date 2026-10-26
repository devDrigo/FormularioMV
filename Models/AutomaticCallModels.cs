using System;
using System.Collections.Generic;

namespace FluigPainel.Models
{
    public class AutomaticCallSource
    {
        public string Id { get; set; } = "fonte-planilha-principal";
        public string Nome { get; set; } = "Planilha principal";
        public string Tipo { get; set; } = "Google Sheets";
        public string Endereco { get; set; } = "";
        public string AbaOuTabela { get; set; } = "";
        public List<string> Abas { get; set; } = new();
        public bool Ativa { get; set; } = true;
    }

    public class AutomaticTriggerCondition
    {
        public string Coluna { get; set; } = "";
        public string Operador { get; set; } = "Igual a";
        public string TipoValor { get; set; } = "Texto";
        public string Valor { get; set; } = "";
        public string ModoData { get; set; } = "Hoje";
        public int PeriodoQuantidade { get; set; } = 1;
        public string PeriodoUnidade { get; set; } = "meses";
    }

    public class AutomaticTriggerRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Nome { get; set; } = "";
        public string Descricao { get; set; } = "";
        public string Condicao { get; set; } = "";
        public string FonteId { get; set; } = "fonte-planilha-principal";
        public string AbaFonte { get; set; } = "";
        public List<AutomaticTriggerCondition> Condicoes { get; set; } = new();
        public string Combinacao { get; set; } = "E";
        public bool Ativa { get; set; } = true;
    }

    public class AutomaticCallConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Nome { get; set; } = "";
        public bool Ativo { get; set; } = true;
        public string ModoGatilho { get; set; } = "Horario";
        public string Periodicidade { get; set; } = "Diariamente";
        public List<string> Horarios { get; set; } = new() { "06:00" };
        public string DiaSemana { get; set; } = "";
        public int DiaMes { get; set; } = 1;
        public int MesAno { get; set; } = 1;
        public string FonteId { get; set; } = "fonte-planilha-principal";
        public string AbaFonte { get; set; } = "";
        public string RegraId { get; set; } = "";
        public string Categoria { get; set; } = "";
        public string Subcategoria { get; set; } = "";
        public int PrazoHoras { get; set; } = 24;
        public string Titulo { get; set; } = "";
        public string Descricao { get; set; } = "";
        public string ColunaNumeroChamado { get; set; } = "";
        public string ColunaNumeroChamadoFonteId { get; set; } = "fonte-planilha-principal";
        public string ColunaNumeroChamadoAba { get; set; } = "";
        public string Tipo { get; set; } = "PADRAO";
        public DateTime? UltimaExecucao { get; set; }
        public DateTime? ProximaExecucao { get; set; }
    }

    public class AutomaticCallHistory
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ConfigId { get; set; } = "";
        public string ConfigNome { get; set; } = "";
        public string NumeroChamado { get; set; } = "";
        public string Referencia { get; set; } = "";
        public DateTime AbertoEm { get; set; } = DateTime.Now;
        public string Status { get; set; } = "Aberto";
        public string Mensagem { get; set; } = "";
    }

    public class AutomaticCallStore
    {
        public List<AutomaticCallConfig> Configuracoes { get; set; } = new();
        public List<AutomaticCallSource> Fontes { get; set; } = new();
        public List<AutomaticTriggerRule> Regras { get; set; } = new();
        public List<AutomaticCallHistory> Historico { get; set; } = new();
        public List<string> Exclusoes { get; set; } = new();
    }

    public class CentralChamadosViewModel
    {
        public List<AutomaticCallConfig> Configuracoes { get; set; } = new();
        public List<AutomaticCallSource> Fontes { get; set; } = new();
        public List<AutomaticTriggerRule> Regras { get; set; } = new();
        public List<AutomaticCallHistory> Historico { get; set; } = new();
        public string? Mensagem { get; set; }
        public string? Erro { get; set; }
    }
}

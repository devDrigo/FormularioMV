using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;

namespace FluigPainel.Models
{
    #region Modelos de Login e UI

    public class LoginModel
    {
        public string? Usuario { get; set; }
        public string? Senha { get; set; }
        public int RefreshSeconds { get; set; } = 30;
        public string Papel { get; set; } = "TI35";
    }

    public class ErrorViewModel
    {
        public string? RequestId { get; set; }
        public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
    }

    #endregion

    #region Modelos de Sessão e Configuração

    public class UserSessionData
    {
        public string JSessionId { get; set; } = "";
        public string JSessionIdSso { get; set; } = "";
        public string ColleagueId { get; set; } = "";
        public DateTime LastActivity { get; set; }
        public string NomeUsuario { get; set; } = "";
        public string JwtToken { get; set; } = "";
        public CookieContainer CookieContainer { get; set; } = new CookieContainer();
        // Reaproveitado entre todas as requisições da mesma sessão (evita abrir uma
        // conexão/handshake TLS novo para cada recurso da página — css, js, imagens,
        // fontes e chamadas ajax das abas Histórico/Anexos/Formulário).
        public HttpClient? Client { get; set; }
    }

    public class UsuarioConfig
    {
        public string Login { get; set; } = "";
        public string Senha { get; set; } = "";
        public string Nome { get; set; } = "";
        public string UserCode { get; set; } = "";
        public string Papel { get; set; } = "Tecnico";
    }

    public static class ConfiguracaoSistema
    {
        public static readonly List<UsuarioConfig> UsuariosPadrao = CarregarUsuarios();

        private static List<UsuarioConfig> CarregarUsuarios()
        {
            var usuarios = new List<UsuarioConfig>();
            Adicionar(usuarios, "USUARIO_TECNICO", "Tecnico", 15);
            Adicionar(usuarios, "USUARIO_ANALISTA", "Analista", 10);
            return usuarios;
        }

        private static void Adicionar(List<UsuarioConfig> destino, string prefixo, string papel, int limite)
        {
            for (var i = 1; i <= limite; i++)
            {
                var login = Environment.GetEnvironmentVariable(prefixo + i);
                var senha = Environment.GetEnvironmentVariable("SENHA_" + prefixo.Substring("USUARIO_".Length) + i);
                if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(senha)) continue;
                var nome = Environment.GetEnvironmentVariable("NOME_" + prefixo.Substring("USUARIO_".Length) + i);
                destino.Add(new UsuarioConfig { Login = login.Trim(), Senha = senha, Nome = string.IsNullOrWhiteSpace(nome) ? login.Trim() : nome.Trim(), UserCode = login.Trim(), Papel = papel });
            }
        }

        public static string FilaPadrao { get; set; } = "TI35";
        public static int RefreshSecondsPadrao { get; set; } = 30;
    }

    #endregion

    #region Modelos de Resposta (Original)

    public class FluigResponse
    {
        public bool Success { get; set; }
        public List<Solicitacao> Solicitacoes { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public string? UsuarioLogado { get; set; }
        public int RefreshSeconds { get; set; } = 30;
        public Dictionary<string, int> ContagemPorUsuario { get; set; } = new();
    }

    #endregion

    #region Modelo de Solicitação (Base)

    public class Solicitacao
    {
        public string Codigo { get; set; } = "";
        public string Detalhe { get; set; } = "";
        public string Solicitante { get; set; } = "";
        public string Setor { get; set; } = "";
        public string Abertura { get; set; } = "";
        public string PrazoTarefa { get; set; } = "";
        public DateTime? DataAbertura { get; set; }
        public string Estado { get; set; } = "";
        public string Responsavel { get; set; } = "";
        public string ResponsavelLogin { get; set; } = "";
        public string TipoAtendimento { get; set; } = "";
    }

    #endregion

    #region Modelo Unificado

    public class FluigResponseUnificado
    {
        public bool FluigOffline { get; set; } = false;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? UsuarioLogado { get; set; }
        public int RefreshSeconds { get; set; } = 30;
        public string FilaAtual { get; set; } = "TI35";

        public List<Solicitacao> AguardandoAtendimento { get; set; } = new();
        public List<Solicitacao> EmAtendimento { get; set; } = new();
        public List<Solicitacao> ChamadosDoUsuarioLogado { get; set; } = new();
        public List<ContagemUsuario> ContagensUsuarios { get; set; } = new();

        public int TotalGeral => AguardandoAtendimento.Count + EmAtendimento.Count;
        public int TotalAguardando => AguardandoAtendimento.Count;
        public int TotalEmAtendimento => EmAtendimento.Count;
    }

    public class ContagemUsuario
    {
        public string Nome { get; set; } = "";
        public int Hoje { get; set; }
        public int Ontem { get; set; }
    }

    #endregion
}

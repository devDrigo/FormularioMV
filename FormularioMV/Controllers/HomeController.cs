using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.AspNetCore.Mvc;
using MimeKit;
using MailKit.Net.Smtp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SeuProjeto.Controllers
{
    public class HomeController : Controller
    {
        private readonly string _spreadsheetId = "1SMBMTjjHdqBXYhVPBmDmvcwxRhLU4-8Ai1bYIRV0GoU";

        public IActionResult Index()
        {
            return View();
        }

        private async Task EnviarEmailAsync(List<string> destinatarios, string assunto, string corpo)
        {

            
        }

        [HttpPost]
        public async Task<IActionResult> Enviar(Microsoft.AspNetCore.Http.IFormCollection form)
        {
            try
            {
                var credentialsJson = Environment.GetEnvironmentVariable("GOOGLE_SHEETS_CREDENTIALS_JSON");
                if (string.IsNullOrEmpty(credentialsJson))
                {
                    TempData["Mensagem"] = "Credenciais do Google Sheets não configuradas!";
                    return RedirectToAction("Index");
                }

                GoogleCredential credential;
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(credentialsJson)))
                {
                    credential = GoogleCredential.FromStream(stream).CreateScoped(SheetsService.Scope.Spreadsheets);
                }

                var service = new SheetsService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "Solicitacao Transporte",
                });

                string Get(string key) => form.TryGetValue(key, out var v) ? v.ToString() : "";
                bool Has(string key) => form.TryGetValue(key, out _);
                bool satNaoAval = Has("satNaoAval");

                var dados = new List<object>
                {
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    $"{Get("fc")} bpm",
                    $"{Get("fr")} ipm",
                    $"{Get("tax")} °C",
                    $"{Get("pa")} mmHg",
                    satNaoAval ? "Não avaliado" : $"{Get("sat")} %",
                    satNaoAval ? "Sim" : "Não",
                    Get("sensorio"),
                    Get("debito_urina"),
                    Get("suporte_o2"),
                    Has("fluxo") ? $"{Get("fluxo")} L/min" : "",
                    Has("fio2") ? $"{Get("fio2")} %" : "",
                    Has("sat_vent") ? $"{Get("sat_vent")} %" : "",
                    Has("peep") ? $"{Get("peep")} cmH₂O" : "",
                    Has("sat_cpap") ? $"{Get("sat_cpap")} %" : "",
                    Has("peep_cpap") ? $"{Get("peep_cpap")} cmH₂O" : "",
                    Get("tipo_transporte"),
                    Get("isolamento"),
                    Get("germe"),
                    Get("vaso"),
                    Get("nome_vaso"),
                    Get("dose_vaso"),
                    Get("evolucao"),
                    Get("origem"),
                    Get("destino"),
                    Get("dt_alta"),
                    Get("contato_nome"),
                    Get("contato_tel")
                };

                var range = "NaoRenomear!A1";
                var valueRange = new ValueRange { Values = new List<IList<object>> { dados } };

                var appendRequest = service.Spreadsheets.Values.Append(valueRange, _spreadsheetId, range);
                appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
                await appendRequest.ExecuteAsync();

                // Monta corpo do e-mail
                var corpoEmail = new StringBuilder();
                corpoEmail.AppendLine("🩺 *Nova Solicitação de Transporte Registrada* 🩺\n");
                corpoEmail.AppendLine($"Data/Hora: {DateTime.Now:dd/MM/yyyy HH:mm}\n");
                corpoEmail.AppendLine($"FC: {Get("fc")} bpm");
                corpoEmail.AppendLine($"FR: {Get("fr")} ipm");
                corpoEmail.AppendLine($"Temp: {Get("tax")} °C");
                corpoEmail.AppendLine($"PA: {Get("pa")} mmHg");
                corpoEmail.AppendLine($"SatO₂: {(satNaoAval ? "Não avaliado" : $"{Get("sat")} %")}");
                corpoEmail.AppendLine($"Sensorio: {Get("sensorio")}");
                corpoEmail.AppendLine($"Débito urinário: {Get("debito_urina")}");
                corpoEmail.AppendLine($"Suporte O₂: {Get("suporte_o2")}");
                if (Has("fluxo")) corpoEmail.AppendLine($"Fluxo: {Get("fluxo")} L/min");
                if (Has("fio2")) corpoEmail.AppendLine($"FiO₂: {Get("fio2")} %");
                if (Has("sat_vent")) corpoEmail.AppendLine($"Sat Ventilada: {Get("sat_vent")} %");
                if (Has("peep")) corpoEmail.AppendLine($"PEEP: {Get("peep")} cmH₂O");
                if (Has("sat_cpap")) corpoEmail.AppendLine($"Sat CPAP: {Get("sat_cpap")} %");
                if (Has("peep_cpap")) corpoEmail.AppendLine($"PEEP CPAP: {Get("peep_cpap")} cmH₂O");

                corpoEmail.AppendLine($"\nTipo Transporte: {Get("tipo_transporte")}");
                corpoEmail.AppendLine($"Isolamento: {Get("isolamento")}");
                corpoEmail.AppendLine($"Germe: {Get("germe")}");
                corpoEmail.AppendLine($"Vasoativo: {Get("vaso")}");
                corpoEmail.AppendLine($"Nome droga: {Get("nome_vaso")}");
                corpoEmail.AppendLine($"Dose: {Get("dose_vaso")}");
                corpoEmail.AppendLine($"\nEvolução: {Get("evolucao")}");
                corpoEmail.AppendLine($"Origem: {Get("origem")}");
                corpoEmail.AppendLine($"Destino: {Get("destino")}");
                corpoEmail.AppendLine($"Data Alta: {Get("dt_alta")}");
                corpoEmail.AppendLine($"Contato: {Get("contato_nome")} - {Get("contato_tel")}");
                corpoEmail.AppendLine("\n--\nSistema Solicitação Transporte");

                // Envia o e-mail
                var destinatarios = new List<string>
                {
                    "rodrigogd30@gmail.com", // principal
                    "solicitacaomv@gmail.com" // cópia
                };

                await EnviarEmailAsync(destinatarios, "🚑 Nova Solicitação de Transporte", corpoEmail.ToString());

                TempData["Mensagem"] = "Solicitação enviada com sucesso!";
            }
            catch (Exception ex)
            {
                TempData["Mensagem"] = $"Erro ao enviar solicitação: {ex.Message}";
            }

            return RedirectToAction("Index");
        }
    }
}

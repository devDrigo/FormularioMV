using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.AspNetCore.Mvc;
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

                // Helper para pegar valor ou string vazia
                string Get(string key) => form.TryGetValue(key, out var v) ? v.ToString() : "";

var dados = new List<object>
    {
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        $"{form["fc"]} bpm",
        $"{form["fr"]} ipm",
        $"{form["tax"]} °C",
        $"{form["pa"]} mmHg",
        form.ContainsKey("satNaoAval") ? "Não avaliado" : $"{form["sat"]} %",
        form.ContainsKey("satNaoAval") ? "Sim" : "Não",
        form["sensorio"],
        form["debito_urina"],
        form["suporte_o2"],

        // campos opcionais suporte O2
        form.ContainsKey("fluxo") ? $"{form["fluxo"]} L/min" : "",
        form.ContainsKey("fio2") ? $"{form["fio2"]} %" : "",
        form.ContainsKey("sat_vent") ? $"{form["sat_vent"]} %" : "",
        form.ContainsKey("peep") ? $"{form["peep"]} cmH₂O" : "",
        form.ContainsKey("sat_cpap") ? $"{form["sat_cpap"]} %" : "",
        form.ContainsKey("peep_cpap") ? $"{form["peep_cpap"]} cmH₂O" : "",

        form["isolamento"],
        form["germe"],
        form["vaso"],
        form["nome_vaso"],
        form["dose_vaso"],
        form["evolucao"],
        form["origem"],
        form["destino"],
        form["dt_alta"],
        form["contato_nome"],
        form["contato_tel"]
    };

                var range = "NaoRenomear!A1";

                var valueRange = new ValueRange
                {
                    Values = new List<IList<object>> { dados }
                };

                var appendRequest = service.Spreadsheets.Values.Append(valueRange, _spreadsheetId, range);
                appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;

                await appendRequest.ExecuteAsync();

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

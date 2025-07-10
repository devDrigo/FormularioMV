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

        // Métodos auxiliares
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

        // Debug log (opcional)
        System.Diagnostics.Debug.WriteLine("Dados a enviar: " + string.Join(", ", dados));

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

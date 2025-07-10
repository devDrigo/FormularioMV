using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;

namespace SeuProjeto.Controllers
{
    public class HomeController : Controller
    {
        // Coloque aqui o ID da sua planilha (pegue da URL da planilha no Google Sheets)
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

var dados = new List<object>
{
    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),  // Data e hora atual
    "80",    // FC
    "18",    // FR
    "36.5",  // Tax
    "120/80",// PA
    "98",    // Sat
    "Não",   // SatNaoAvaliado
    "Alerta",// Sensorio
    "Normal",// DebitoUrinario
    "Ar ambiente", // SuporteO2
    "", "", "", "", "", "", // Campos de suporte O2 opcionais (fluxo, fio2, etc)
    "Não",   // Isolamento
    "",      // Germe
    "Não",   // DrogasVasoativas
    "",      // NomeDroga
    "",      // Dosagem
    "Paciente está estável.", // Evolucao
    "Unidade A",  // Origem
    "Unidade B",  // Destino
    DateTime.Now.AddHours(2).ToString("yyyy-MM-ddTHH:mm"), // DataHoraAlta (formato datetime-local)
    "Dr. Fulano", // ProfissionalResponsavel
    "(11) 99999-9999" // TelefoneContato
};



        var range = "NaoRenomear!A1"; // Ajuste se necessário para o nome da sua aba

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

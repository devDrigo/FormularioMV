using Microsoft.AspNetCore.Mvc;
using OfficeOpenXml;
using System.IO;
using MimeKit;
using MailKit.Net.Smtp;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FormsMV.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        public async Task EnviarEmailAsync(List<string> destinatarios, string assunto, string mensagemCorpo)
        {
            var mensagem = new MimeMessage();
            mensagem.From.Add(new MailboxAddress("Nova Solicitação MV", "solicitacaomv@gmail.com"));
            
            foreach (var destinatario in destinatarios)
            {
                mensagem.To.Add(new MailboxAddress("", destinatario));
            }
            
            mensagem.Subject = assunto;

            mensagem.Body = new TextPart("plain")
            {
                Text = mensagemCorpo
            };

            using (var cliente = new MailKit.Net.Smtp.SmtpClient())
            {
                try
                {
                    await cliente.ConnectAsync("smtp.gmail.com", 587, MailKit.Security.SecureSocketOptions.StartTls);
                    await cliente.AuthenticateAsync("solicitacaomv@gmail.com", "qleewbvdakggmvrk");
                    await cliente.SendAsync(mensagem);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erro ao enviar e-mail: {ex.Message}");
                    ViewBag.EmailErro = "Erro ao enviar o e-mail. Por favor, tente novamente.";
                }
                finally
                {
                    await cliente.DisconnectAsync(true);
                }
            }
        }

        [HttpPost]
        public async Task<IActionResult> Verificar(string nome, string sexo, DateTime dataNascimento, string cpf, string rg, string orgaoEmissor, string nomeMae, string nomePai, string endereco, string cep, string funcao, string possuiConselho, string numeroConselho, string usuarioRede, string usuarioMV, string setorLotacao, string cargaHoraria, string cartaoSus, string usuarioEspelho, string email)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            string caminhoArquivo = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "usuarios.xlsx");

            if (System.IO.File.Exists(caminhoArquivo))
            {
                using (var pacote = new ExcelPackage(new FileInfo(caminhoArquivo)))
                {
                    ExcelWorksheet planilha = pacote.Workbook.Worksheets[0];
                    int totalLinhas = planilha.Dimension.Rows;

                    bool cpfEncontrado = false;

                    for (int i = 2; i <= totalLinhas; i++) // Começa na linha 2 para ignorar o cabeçalho
                    {
                        var cpfPlanilha = planilha.Cells[i, 12].Value?.ToString();

                        if (cpfPlanilha == cpf)
                        {
                            cpfEncontrado = true;
                            break;
                        }
                    }

                    string corpoEmail = $"Nome Completo: {nome}\n" +
                                        $"Sexo: {sexo}\n" +
                                        $"Data de Nascimento: {dataNascimento:dd/MM/yyyy}\n" +
                                        $"CPF: {cpf}\n" +
                                        $"RG: {rg}\n" +
                                        $"Órgão Emissor e UF: {orgaoEmissor}\n" +
                                        $"Nome da Mãe: {nomeMae}\n" +
                                        $"Nome do Pai: {nomePai}\n" +
                                        $"Endereço: {endereco}\n" +
                                        $"CEP: {cep}\n" +
                                        $"Função: {funcao}\n" +
                                        $"Possui Conselho: {possuiConselho}\n" +
                                        $"{(possuiConselho == "Sim" ? $"Número do Conselho: {numeroConselho}\n" : "")}" +
                                        $"Setor de Lotação: {setorLotacao}\n" +
                                        $"Carga Horária: {cargaHoraria}\n" +
                                        $"Cartão SUS: {cartaoSus}\n" +
                                        $"E-mail Pessoal: {email}";

                    if (true)
                    {
                        ViewBag.Mensagem = "Formulário enviado com sucesso!";

                        var destinatarios = new List<string>
                        {
                            "ti.f35@hmtjgo.org.br",
                            email
                        };

                        await EnviarEmailAsync(destinatarios, $"Criação de Usuário MV ({cpf})", corpoEmail);

                        return RedirectToAction("Index");
                    }
                    else
                    {
                        ViewBag.CpfNaoEncontrado = true;
                        return View("Index");
                    }
                }
            }

            ViewBag.CpfNaoEncontrado = true;
            return View("Index");
        }
    }
}

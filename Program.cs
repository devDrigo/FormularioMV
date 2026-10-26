using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Configuração para o Render (ele define a PORT environment variable)
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://*:{port}");

// Adicionar serviços ao container
builder.Services.AddControllersWithViews();
var dataProtectionPath = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_DIR");
if (string.IsNullOrWhiteSpace(dataProtectionPath)) dataProtectionPath = Path.Combine(AppContext.BaseDirectory, "data", "keys");
Directory.CreateDirectory(dataProtectionPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("FluigPainel");
builder.Services.AddHttpClient();
    builder.Services.AddSingleton<FluigPainel.Services.AutomaticCallStoreService>();
    builder.Services.AddSingleton<FluigPainel.Services.AutomaticCallScheduler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FluigPainel.Services.AutomaticCallScheduler>());
    builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Adicionar suporte a sessão (necessário para login)
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = "FluigSolicitacoes.Session.v3";
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// Adicionar suporte a cookies de autenticação
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "Cookies";
    options.DefaultChallengeScheme = "Cookies";
    options.DefaultSignInScheme = "Cookies";
})
.AddCookie("Cookies", options =>
{
    options.Cookie.Name = "FluigSolicitacoes.Auth.v3";
    options.Cookie.HttpOnly = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/Home/Login";
    options.LogoutPath = "/Home/Logout";
    options.AccessDeniedPath = "/Home/Login";
});

var app = builder.Build();

// Configurar pipeline HTTP
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Habilitar arquivos estáticos
app.UseStaticFiles();

// Habilitar roteamento
app.UseRouting();

// Habilitar autenticação e autorização
app.UseAuthentication();
app.UseAuthorization();

// Habilitar sessão
app.UseSession();

// Habilitar endpoints com rotas por atributo (proxy do Fluig)
app.MapControllers();

// MUDANÇA: Rota padrão agora aponta para Index em vez de Login
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}"); // Agora inicia no Index

app.Run();

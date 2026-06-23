using System.Text.Json.Serialization;
using ClaudeCodeServer.Auth;
using ClaudeCodeServer.Hubs;
using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSignalR()
    .AddJsonProtocol(o =>
        o.PayloadSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton<ProjectManager>();
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<FileWatcherService>();
builder.Services.AddSingleton<ChatHistoryService>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<ApiKeyAuthService>();

// Аутентификация по единственному API-ключу (Bearer / X-Api-Key / ?access_token=)
builder.Services.AddAuthentication(ApiKeyAuthService.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthService.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true)
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));

var app = builder.Build();

// Прогрев сервиса ключа на старте — печатает сгенерированный ключ в консоль
app.Services.GetRequiredService<ApiKeyAuthService>();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Раздача фронтенда из frontend/dist/ (production / PWA)
var distPath = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(), "..", "..", "frontend", "dist"));
if (Directory.Exists(distPath))
{
    var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distPath);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fp });
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fp });
}

app.MapControllers();
app.MapHub<SessionHub>("/hubs/session");

app.Run();

public partial class Program { }

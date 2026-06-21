using System.Text.Json.Serialization;
using ClaudeCodeServer.Hubs;
using ClaudeCodeServer.Services;

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
builder.Services.AddSingleton<ChatHistoryService>();
builder.Services.AddSingleton<SessionManager>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173")
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));

var app = builder.Build();

app.UseCors();

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

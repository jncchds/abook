using ABook.Agents;
using ABook.Api.Hubs;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Infrastructure.Data;
using ABook.Infrastructure.Llm;
using ABook.Infrastructure.Repositories;
using ABook.Infrastructure.VectorStore;
using Microsoft.EntityFrameworkCore;
using Qdrant.Client;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=abook;Username=abook;Password=abook";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── Qdrant ────────────────────────────────────────────────────────────────────
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "localhost";
var qdrantPort = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334");
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort));
builder.Services.AddSingleton<IVectorStoreService, QdrantVectorStoreService>();

// ── Core services ─────────────────────────────────────────────────────────────
builder.Services.AddScoped<IBookRepository, BookRepository>();
builder.Services.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();

// ── SignalR ───────────────────────────────────────────────────────────────────
builder.Services.AddSignalR();
builder.Services.AddScoped<IBookNotifier, SignalRBookNotifier>();

// ── Agents ────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<PlannerAgent>();
builder.Services.AddScoped<WriterAgent>();
builder.Services.AddScoped<EditorAgent>();
builder.Services.AddScoped<ContinuityCheckerAgent>();
builder.Services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();

// ── API ───────────────────────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── CORS (dev: allow Vite dev server) ─────────────────────────────────────────
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173")
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));

var app = builder.Build();

// ── Migrations on startup ─────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapHub<BookHub>("/hubs/book");

// React SPA fallback — serve index.html for all non-API routes
app.MapFallbackToFile("index.html");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

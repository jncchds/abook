using ABook.Agents;
using ABook.Api.Auth;
using ABook.Api.Hubs;
using ABook.Api.Mcp;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Data;
using ABook.Infrastructure.Llm;
using ABook.Infrastructure.Repositories;
using ABook.Infrastructure.VectorStore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Load optional local overrides (not committed to Git)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=abook;Username=abook;Password=abook";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, o => o.UseVector()));

// ── Vector store (pgvector via PostgreSQL) ────────────────────────────────────
builder.Services.AddScoped<IVectorStoreService, PgvectorVectorStoreService>();

// ── Core services ─────────────────────────────────────────────────────────────
builder.Services.AddScoped<IBookRepository, BookRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddHttpClient("ollama");
builder.Services.AddHttpContextAccessor();

// ── Auth (cookie-based + ApiToken for MCP) ────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>("ApiToken", null);
builder.Services.AddAuthorization();

// ── SignalR ───────────────────────────────────────────────────────────────────
builder.Services.AddSignalR()
    .AddJsonProtocol(o =>
    {
        o.PayloadSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        o.PayloadSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddScoped<IBookNotifier, SignalRBookNotifier>();

// ── Agents ────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ABook.Agents.AgentRunStateService>();
builder.Services.AddScoped<ABook.Agents.QuestionAgent>();
builder.Services.AddScoped<ABook.Agents.StoryBibleAgent>();
builder.Services.AddScoped<ABook.Agents.CharactersAgent>();
builder.Services.AddScoped<ABook.Agents.PlotThreadsAgent>();
builder.Services.AddScoped<ABook.Agents.PlannerAgent>();
builder.Services.AddScoped<ABook.Agents.WriterAgent>();
builder.Services.AddScoped<ABook.Agents.EditorAgent>();
builder.Services.AddScoped<ABook.Agents.ContinuityCheckerAgent>();
builder.Services.AddScoped<IAgentOrchestrator, ABook.Agents.AgentOrchestrator>();
builder.Services.AddHostedService<ABook.Api.HostedServices.RunRecoveryService>();

// ── MCP Server ────────────────────────────────────────────────────────────────
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<UserMcpTools>()
    .WithTools<BookMcpTools>()
    .WithTools<ContentMcpTools>()
    .WithTools<AgentMcpTools>();

// ── API ───────────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        o.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

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
    app.Logger.LogInformation("Applying database migrations…");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Database migrations applied.");

    // Seed global LLM defaults from env / appsettings ──────────────────────────
    var llmSection = app.Configuration.GetSection("LlmDefaults");
    var providerStr = llmSection["Provider"];
    if (!string.IsNullOrWhiteSpace(providerStr) &&
        Enum.TryParse<LlmProvider>(providerStr, ignoreCase: true, out var provider))
    {
        var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
        var existing = await repo.GetLlmConfigAsync(null, null);
        var config = existing ?? new LlmConfiguration();
        config.Provider = provider;
        var modelName = llmSection["ModelName"];
        if (!string.IsNullOrWhiteSpace(modelName)) config.ModelName = modelName;
        var endpoint = llmSection["Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint)) config.Endpoint = endpoint;
        var apiKey = llmSection["ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey)) config.ApiKey = apiKey;
        var embeddingModel = llmSection["EmbeddingModelName"];
        if (!string.IsNullOrWhiteSpace(embeddingModel)) config.EmbeddingModelName = embeddingModel;
        await repo.UpsertLlmConfigAsync(config);
        app.Logger.LogInformation(
            "LLM default config: provider={Provider} model={Model} endpoint={Endpoint}",
            config.Provider, config.ModelName, config.Endpoint);
    }
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapHub<BookHub>("/hubs/book");

// ── MCP endpoint (authenticated via ApiToken Bearer or session cookie) ────────
app.MapMcp("/mcp")
   .RequireAuthorization(policy => policy
       .AddAuthenticationSchemes("ApiToken", CookieAuthenticationDefaults.AuthenticationScheme)
       .RequireAuthenticatedUser());

// React SPA fallback — serve index.html for all non-API routes
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("ABook API starting.");
app.Run();

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
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Load optional local overrides (not committed to Git)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=abook;Username=abook;Password=abook";

// `dotnet ABook.Api.dll secrets show` — print database-stored secrets as env vars, then exit.
if (SecretsCommand.Matches(args))
    return await SecretsCommand.RunAsync(args, connectionString, builder.Configuration);

// AddDbContextFactory registers IDbContextFactory<AppDbContext> (Singleton) and also
// AppDbContext as a Scoped service, so repositories that inject it directly still work.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
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

// ── Auth (JWT access tokens for the SPA + ApiToken for MCP) ───────────────────
// The browser holds a short-lived access token in memory and refreshes it with an httpOnly cookie that is only
// sent to /api/auth (see JwtTokenService). The signing key is resolved per request because it is only known
// once the database is migrated (JwtKeyService may generate and store it).
builder.Services.AddSingleton<JwtKeyService>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = JwtTokenService.ValidationParameters();
        o.Events = new JwtBearerEvents
        {
            // Browsers cannot set headers on WebSocket/SSE requests, so SignalR passes the token in the query string.
            OnMessageReceived = ctx =>
            {
                if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")
                    && ctx.Request.Query["access_token"] is { Count: > 0 } token)
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>("ApiToken", null);
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtKeyService>((o, keys) =>
        o.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, _, _) => [keys.SigningKey]);
builder.Services.AddAuthorization();

// Shared serializer settings used by both SignalR and MVC controllers
void ConfigureJsonSerializer(System.Text.Json.JsonSerializerOptions opts)
{
    opts.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
}

// ── SignalR ───────────────────────────────────────────────────────────────────
builder.Services.AddSignalR()
    .AddJsonProtocol(o => ConfigureJsonSerializer(o.PayloadSerializerOptions));
builder.Services.AddScoped<IBookNotifier, SignalRBookNotifier>();

// ── Agents ────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ABook.Agents.AgentRunStateService>();
builder.Services.AddSingleton<AgentRunnerService>();
builder.Services.AddSingleton<ABook.Api.Services.ChapterIndexingService>();
builder.Services.AddScoped<ABook.Agents.QuestionAgent>();
builder.Services.AddScoped<ABook.Agents.StoryBibleAgent>();
builder.Services.AddScoped<ABook.Agents.CharactersAgent>();
builder.Services.AddScoped<ABook.Agents.PlotThreadsAgent>();
builder.Services.AddScoped<ABook.Agents.PlannerAgent>();
builder.Services.AddScoped<ABook.Agents.WriterAgent>();
builder.Services.AddScoped<ABook.Agents.EditorAgent>();
builder.Services.AddScoped<ABook.Agents.ContinuityCheckerAgent>();
builder.Services.AddScoped<ABook.Agents.IllustrationPlannerAgent>();
builder.Services.AddScoped<ABook.Agents.IllustrationRenderer>();
builder.Services.AddScoped<ABook.Agents.ChapterSummarizer>();
builder.Services.AddScoped<ABook.Agents.BookContentService>();
builder.Services.AddScoped<ABook.Agents.BookChatAgent>();
builder.Services.AddScoped<ABook.Agents.ProposalExecutor>();
builder.Services.AddSingleton<ABook.Api.Services.BookChatService>();
builder.Services.AddSingleton<IImageGeneratorFactory, ABook.Infrastructure.Imaging.ImageGeneratorFactory>();
builder.Services.AddSingleton<ILlmModelUnloader, ABook.Infrastructure.Imaging.LlmModelUnloader>();
builder.Services.AddScoped<IAgentOrchestrator, ABook.Agents.AgentOrchestrator>();
builder.Services.AddHostedService<ABook.Api.HostedServices.RunRecoveryService>();

// ── MCP Server ────────────────────────────────────────────────────────────────
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<UserMcpTools>()
    .WithTools<BookMcpTools>()
    .WithTools<ContentMcpTools>()
    .WithTools<AgentMcpTools>()
    .WithTools<BookChatMcpTools>()
    .WithTools<IllustrationMcpTools>();
// ── Public mode ───────────────────────────────────────────────────────────────
var isPublicMode = builder.Configuration.GetValue<bool>("PublicMode");
builder.Services.AddSingleton(new ABook.Api.Services.PublicModeOptions(isPublicMode));
builder.Services.AddScoped<ABook.Api.Services.BookExportService>();
builder.Services.AddScoped<ABook.Api.Services.ClarificationService>();
builder.Services.AddScoped<ABook.Api.Services.UserProvisioningService>();
// ── API ───────────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o => ConfigureJsonSerializer(o.JsonSerializerOptions));

// ── Health (for container orchestration / uptime checks) ──────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<ABook.Api.Services.DatabaseHealthCheck>("database");

var app = builder.Build();

var configuredMaxConcurrentRuns = app.Configuration.GetValue<int?>("AgentSettings:MaxConcurrentRuns") ?? 3;
var runStateService = app.Services.GetRequiredService<ABook.Agents.AgentRunStateService>();
runStateService.MaxConcurrentRuns = configuredMaxConcurrentRuns;
app.Logger.LogInformation("Agent run concurrency limit set to {MaxConcurrentRuns}.", runStateService.MaxConcurrentRuns);

// ── Migrations on startup ─────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    app.Logger.LogInformation("Applying database migrations…");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Database migrations applied.");

    await app.Services.GetRequiredService<JwtKeyService>().InitializeAsync(app.Configuration, db, app.Logger);
    var staleSessions = await scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>()
        .DeleteStaleAsync(DateTime.UtcNow.AddDays(-7));
    if (staleSessions > 0)
        app.Logger.LogInformation("Deleted {Count} expired or revoked sign-in sessions.", staleSessions);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapHub<BookHub>("/hubs/book");
app.MapHealthChecks("/health").AllowAnonymous();

// ── MCP endpoint (authenticated via ApiToken Bearer or a browser access token) ──
app.MapMcp("/mcp")
   .RequireAuthorization(policy => policy
       .AddAuthenticationSchemes("ApiToken", JwtBearerDefaults.AuthenticationScheme)
       .RequireAuthenticatedUser());

// React SPA fallback — serve index.html for all non-API routes
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("ABook API starting.");

// TODO: Add OpenTelemetry tracing for agent runs, LLM calls, and RAG queries
// to enable production monitoring and cost tracking.

app.Run();
return 0;

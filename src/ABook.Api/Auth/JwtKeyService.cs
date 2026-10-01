using System.Security.Cryptography;
using System.Text;
using ABook.Core.Models;
using ABook.Infrastructure.Data;
using Microsoft.IdentityModel.Tokens;

namespace ABook.Api.Auth;

/// <summary>
/// Holds the key that signs access tokens. It comes from <c>Jwt__SigningKey</c> when that is set; otherwise a
/// random key is generated once and kept in the <c>ServerSecrets</c> table, so installs that upgrade without
/// adding the env var keep working (and keep their sessions across restarts). A warning is logged at every
/// startup until the key is moved to the environment — see <see cref="SecretsCommand"/>.
/// </summary>
public class JwtKeyService
{
    public const string ConfigKey = "Jwt:SigningKey";
    public const string EnvVarName = "Jwt__SigningKey";
    public const int MinKeyLength = 32;

    private SymmetricSecurityKey? _key;

    public SymmetricSecurityKey SigningKey =>
        _key ?? throw new InvalidOperationException("The JWT signing key has not been initialised.");

    /// <summary>True while the key is the generated one stored in the database rather than an env var.</summary>
    public bool FromDatabase { get; private set; }

    public async Task InitializeAsync(IConfiguration configuration, AppDbContext db, ILogger logger)
    {
        var configured = configuration[ConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.Length < MinKeyLength)
                throw new InvalidOperationException($"{EnvVarName} must be at least {MinKeyLength} characters long.");
            _key = CreateKey(configured);
            FromDatabase = false;
            return;
        }

        var stored = await db.ServerSecrets.FindAsync(ConfigKey);
        if (stored is null)
        {
            stored = new ServerSecret
            {
                Name = ConfigKey,
                Value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
                CreatedAt = DateTime.UtcNow,
            };
            db.ServerSecrets.Add(stored);
            await db.SaveChangesAsync();
            logger.LogWarning("⚠️ {EnvVar} is not set — generated a random JWT signing key and stored it in the database.", EnvVarName);
        }

        _key = CreateKey(stored.Value);
        FromDatabase = true;
        logger.LogWarning(
            "⚠️ The JWT signing key is read from the database because {EnvVar} is not set. Sign-in works, but anyone " +
            "with a database dump can forge sessions. Print it with `docker compose exec abook-api dotnet ABook.Api.dll " +
            "secrets show` and set it as the {EnvVar} environment variable.", EnvVarName, EnvVarName);
    }

    private static SymmetricSecurityKey CreateKey(string secret) => new(Encoding.UTF8.GetBytes(secret));
}

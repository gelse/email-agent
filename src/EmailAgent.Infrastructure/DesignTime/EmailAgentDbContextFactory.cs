using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EmailAgent.Infrastructure.DesignTime;

/// <summary>
/// Design-time entry point for <c>dotnet ef migrations add</c>
/// (<c>--project src/EmailAgent.Infrastructure --startup-project
/// src/EmailAgent.Api</c>). Migrations do not touch the database, so a sane
/// local fallback connection string keeps the command working for
/// contributors without a running stack.
/// </summary>
public sealed class EmailAgentDbContextFactory : IDesignTimeDbContextFactory<EmailAgentDbContext>
{
    /// <summary>
    /// Design-time fallback: <c>migrations add</c> never contacts the
    /// database, so this carries just enough to configure the provider —
    /// deliberately WITHOUT credentials (AGENTS.md: never commit connection
    /// strings or secrets). <c>dotnet ef database update</c> needs the §16
    /// <c>ConnectionStrings__Postgres</c> environment variable instead.
    /// </summary>
    public const string FallbackConnectionString =
        "Host=localhost;Port=5432;Database=emailagent";

    public EmailAgentDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? FallbackConnectionString;

        var options = new DbContextOptionsBuilder<EmailAgentDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new EmailAgentDbContext(options);
    }
}
using EmailAgent.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Infrastructure;

/// <summary>
/// The M1T2 data model: one <see cref="DbSet{TEntity}"/> per §6 table plus
/// <c>refresh_tokens</c> (SQ-1). The context is exposed directly — M1T2
/// deliberately adds no repository layer (task "Out" scope) — M1T6 / M1T7
/// call <c>SaveChangesAsync</c> directly while the codebase is small.
/// </summary>
public class EmailAgentDbContext : DbContext
{
    public EmailAgentDbContext(DbContextOptions<EmailAgentDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<OneTimePassword> OneTimePasswords => Set<OneTimePassword>();

    public DbSet<ModelCatalogEntry> ModelCatalog => Set<ModelCatalogEntry>();

    public DbSet<MessageSource> MessageSources => Set<MessageSource>();

    public DbSet<AgentConfig> AgentConfigs => Set<AgentConfig>();

    public DbSet<ActionConfig> ActionConfigs => Set<ActionConfig>();

    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<PendingAction> PendingActions => Set<PendingAction>();

    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();

    public DbSet<AuditLog> AuditLog => Set<AuditLog>();

    public DbSet<EncryptedBlob> EncryptedBlobs => Set<EncryptedBlob>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    // M1T4 / SQ-10: Identity's role entity (AspNet* mapped by the
    // Configurations/Identity*Map classes). The user-side Identity entities
    // map onto the existing `users` table, so only roles need a DbSet.
    public DbSet<IdentityRole<Guid>> IdentityRoles => Set<IdentityRole<Guid>>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EmailAgentDbContext).Assembly);
    }
}
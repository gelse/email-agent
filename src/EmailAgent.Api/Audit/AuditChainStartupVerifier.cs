using EmailAgent.Core.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailAgent.Api.Audit;

/// <summary>
/// M1T4 task step 4: walks the audit HMAC chain once at startup. A tampering
/// signal is logged as a structured error and never crashes the process —
/// the verifier command from M1T9 is the operator's report.
/// </summary>
public sealed class AuditChainStartupVerifier : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditChainStartupVerifier> _logger;

    public AuditChainStartupVerifier(
        IServiceScopeFactory scopeFactory,
        ILogger<AuditChainStartupVerifier> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            var firstBadId = await audit.VerifyChainAsync(cancellationToken);

            if (firstBadId is not null)
            {
                _logger.LogError(
                    "Audit HMAC chain mismatch at audit_log.id {AuditId}; run tools/AuditVerify (M1T9) for the report.",
                    firstBadId);
            }
        }
        catch (InvalidOperationException ex)
        {
            // No database configured (host boots without a connection string
            // — tests and design-time hosts). Not a tampering signal.
            _logger.LogDebug(ex, "Audit chain startup check skipped: no database configured.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit chain startup check failed; continuing without a verified chain.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

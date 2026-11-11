namespace EmailAgent.Infrastructure.Scheduling;

/// <summary>
/// The unit of work the scheduler (M5T2 producer) hands to the bounded job
/// channel the worker pool (M5T3 consumers) drains — §4's
/// <c>Channel&lt;JobItem&gt;</c> element and §10 job lifecycle step 1's
/// output.
/// </summary>
/// <param name="JobId">
/// The <c>scheduled_jobs.id</c> of the committed row — M5T3's DB claim stays
/// authoritative; the channel item is the wake hint that names it.
/// </param>
/// <param name="SourceId">The owning <c>message_sources.id</c> (per-source cadence / diagnostics).</param>
/// <param name="UserId">
/// The owning user — M5T3's per-user weighted fair queueing (Q-O3) groups on
/// it without a second lookup.
/// </param>
public record JobItem(Guid JobId, Guid SourceId, Guid UserId);

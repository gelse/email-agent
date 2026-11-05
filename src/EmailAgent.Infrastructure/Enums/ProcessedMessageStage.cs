namespace EmailAgent.Infrastructure.Enums;

/// <summary>
/// §6 <c>processed_messages.stage</c>: <c>text NOT NULL DEFAULT 'fetched'</c>
/// with the lowercase values (<c>fetched</c> | <c>prefiltered</c> |
/// <c>decided</c> | <c>acted</c>) per Q-O9. The member names are lowercase on
/// purpose: the column is mapped with <c>HasConversion&lt;string&gt;()</c>,
/// which stores the C# enum *name* verbatim, and M1T2 task step 8 pins the
/// stored values to §6's spec.
/// </summary>
public enum ProcessedMessageStage
{
    fetched,
    prefiltered,
    decided,
    acted,
}
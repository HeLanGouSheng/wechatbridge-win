namespace Bridge.Core.LlmSocial;

/// <summary>One message as llmsocial's generic webhook wants it (see llmsocial
/// src/server/connectors/local.ts). <see cref="Timestamp"/> is epoch milliseconds — llmsocial compares
/// it with its own millisecond clock, seconds would land every message in 1970.</summary>
public sealed record InboundPayload(
    string MessageId,
    string Kind,
    string ThreadId,
    string ThreadTitle,
    ContactRef Contact,
    string Text,
    long Timestamp,
    bool FromSelf);

public sealed record ContactRef(string Id, string Name);

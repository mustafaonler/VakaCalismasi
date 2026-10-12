namespace AtmCash.Application.Outbox;

/// <summary>
/// An event waiting to be published (transactional outbox, B10). It is inserted by the same
/// SaveChanges, and therefore the same database transaction, as the change that raised it, so an
/// event exists if and only if its operation committed. A background worker publishes it later.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        Type = null!;
        Payload = null!;
    }

    public OutboxMessage(string type, string payload, DateTimeOffset occurredAt)
    {
        Id = Guid.CreateVersion7();
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }

    /// <summary>Event name, e.g. "LowCashAlert".</summary>
    public string Type { get; private set; }

    /// <summary>The event serialized as JSON.</summary>
    public string Payload { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>When the worker published the event; <c>null</c> while pending.</summary>
    public DateTimeOffset? ProcessedAt { get; private set; }

    public void MarkProcessed(DateTimeOffset now) => ProcessedAt = now;
}

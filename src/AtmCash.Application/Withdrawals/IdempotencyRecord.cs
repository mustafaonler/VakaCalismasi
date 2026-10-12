namespace AtmCash.Application.Withdrawals;

/// <summary>
/// The stored outcome of a confirm request, keyed by its Idempotency-Key header. A repeated request
/// with the same key returns this response instead of debiting the cassettes again (B4).
/// </summary>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
        Key = null!;
        Response = null!;
    }

    public IdempotencyRecord(string key, Guid quoteId, string response, DateTimeOffset createdAt)
    {
        Key = key;
        QuoteId = quoteId;
        Response = response;
        CreatedAt = createdAt;
    }

    public string Key { get; private set; }

    public Guid QuoteId { get; private set; }

    /// <summary>The serialized <see cref="WithdrawalResultDto"/>.</summary>
    public string Response { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

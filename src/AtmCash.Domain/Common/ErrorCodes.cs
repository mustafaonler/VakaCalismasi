namespace AtmCash.Domain.Common;

public static class ErrorCodes
{
    public const string AtmNotFound = "ATM_NOT_FOUND";
    public const string AtmCodeTaken = "ATM_CODE_TAKEN";
    public const string InvalidAtmCode = "INVALID_ATM_CODE";
    public const string InvalidStateTransition = "INVALID_STATE_TRANSITION";
    public const string OperationNotAllowedInState = "OPERATION_NOT_ALLOWED_IN_STATE";
    public const string InvalidCassetteConfiguration = "INVALID_CASSETTE_CONFIGURATION";
    public const string InvalidDenomination = "INVALID_DENOMINATION";
    public const string ForeignCassetteCount = "FOREIGN_CASSETTE_COUNT";
    public const string CassetteNotFound = "CASSETTE_NOT_FOUND";
    public const string CassetteNotEmpty = "CASSETTE_NOT_EMPTY";
    public const string CassetteCapacityExceeded = "CASSETTE_CAPACITY_EXCEEDED";
    public const string DepositCassetteNotLoadable = "DEPOSIT_CASSETTE_NOT_LOADABLE";
    public const string InvalidQuantity = "INVALID_QUANTITY";
    public const string InsufficientNotes = "INSUFFICIENT_NOTES";
    public const string InvalidAmount = "INVALID_AMOUNT";
    public const string InvalidCardNumber = "INVALID_CARD_NUMBER";
    public const string NoPayableAmount = "NO_PAYABLE_AMOUNT";
    public const string DailyLimitExceeded = "DAILY_LIMIT_EXCEEDED";
    public const string QuoteNotFound = "QUOTE_NOT_FOUND";
    public const string QuoteExpired = "QUOTE_EXPIRED";
    public const string QuoteCancelled = "QUOTE_CANCELLED";
    public const string QuoteAlreadyConfirmed = "QUOTE_ALREADY_CONFIRMED";
    public const string QuoteOptionRequired = "QUOTE_OPTION_REQUIRED";
    public const string QuoteOptionNotOffered = "QUOTE_OPTION_NOT_OFFERED";
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
}

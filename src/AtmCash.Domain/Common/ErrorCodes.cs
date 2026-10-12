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
}

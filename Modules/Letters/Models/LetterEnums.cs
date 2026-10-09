namespace SignIt.Modules.Letters.Models;

public enum LetterStatus
{
    Draft = 1,
    InProgress = 2,
    NeedsRevision = 3,
    AwaitingResourceResolution = 4,
    Finalizing = 5,
    ProcessingFailed = 6,
    Completed = 7,
    Rejected = 8,
    Cancelled = 9,
    Revoked = 10
}

public enum LetterRole
{
    Applicant = 1,
    ClosingSignatory = 2,
    AcknowledgingSignatory = 3,
    ApprovingSignatory = 4
}

public enum DocumentKind
{
    Source = 1,
    Review = 2,
    Final = 3,
    Template = 4
}

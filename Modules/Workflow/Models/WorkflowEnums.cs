namespace SignIt.Modules.Workflow.Models;

public enum WorkflowActionType
{
    Sign = 1,
    Acknowledge = 2,
    ApproveAndSign = 3,
    Review = 4
}

public enum WorkflowTaskStatus
{
    Pending = 1,
    Active = 2,
    Signed = 3,
    Acknowledged = 4,
    Approved = 5,
    RevisionRequested = 6,
    Rejected = 7,
    Deferred = 8,
    Cancelled = 9,
    Superseded = 10
}

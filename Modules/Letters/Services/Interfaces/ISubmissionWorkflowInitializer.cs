using SignIt.Modules.Letters.Models;
using SignIt.Modules.Routing.Services;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Letters.Services;

public sealed record InitializedWorkflow(
    IReadOnlyList<LetterParticipant> Participants,
    IReadOnlyList<WorkflowTask> Tasks,
    WorkflowTask? FirstTask);

public interface ISubmissionWorkflowInitializer
{
    InitializedWorkflow Initialize(
        Guid revisionId,
        IReadOnlyList<RoutingStage> stages,
        IReadOnlyList<SignatureSlot> slots,
        DateTimeOffset now,
        int slaDays);
}

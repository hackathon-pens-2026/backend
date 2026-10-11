using SignIt.Modules.Letters.Models;
using SignIt.Modules.Routing.Services;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Letters.Services;

public sealed class SubmissionWorkflowInitializer : ISubmissionWorkflowInitializer
{
    public InitializedWorkflow Initialize(
        Guid revisionId,
        IReadOnlyList<RoutingStage> stages,
        IReadOnlyList<SignatureSlot> slots,
        DateTimeOffset now,
        int slaDays)
    {
        var participants = new List<LetterParticipant>(stages.Count);
        var tasks = new List<WorkflowTask>(stages.Count);
        WorkflowTask? firstTask = null;
        var dueAt = now.AddDays(slaDays);

        foreach (var stage in stages)
        {
            var slot = slots.Single(x => x.PositionCode == stage.PositionCode);
            var participant = LetterParticipant.Create(
                Guid.NewGuid(),
                revisionId,
                stage.Order == 1 ? LetterRole.Applicant : stage.Order == 2 ? LetterRole.ClosingSignatory : LetterRole.ApprovingSignatory,
                stage.PositionCode,
                stage.UserId,
                stage.Name,
                stage.PositionName,
                true,
                slot.PageIndex,
                slot.X,
                slot.Y,
                slot.Width,
                slot.Height);
            participants.Add(participant);

            var isFirst = stage.Order == 1;
            var task = WorkflowTask.Create(
                Guid.NewGuid(),
                revisionId,
                participant.Id,
                stage.Order,
                stage.Order <= 2 ? WorkflowActionType.Sign : WorkflowActionType.ApproveAndSign,
                stage.UserId,
                stage.PositionCode,
                isFirst ? WorkflowTaskStatus.Active : WorkflowTaskStatus.Pending,
                isFirst ? now : null,
                dueAt);

            if (isFirst)
            {
                firstTask = task;
            }

            tasks.Add(task);
        }

        return new InitializedWorkflow(participants, tasks, firstTask);
    }
}

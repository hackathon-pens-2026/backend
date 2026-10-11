using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Workflow.Services;

public interface IWorkflowMutationStrategy
{
    string Action { get; }
    Task<Guid?> ExecuteAsync(TaskContext context, Guid actor, WorkflowMutationRequest request, DateTimeOffset now, CancellationToken ct);
}

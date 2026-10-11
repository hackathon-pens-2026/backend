using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Workflow.Services;

public interface IWorkflowTaskDtoMapper
{
    Task<WorkflowTaskDto> MapAsync(TaskContext context, Guid actor, bool canAct, CancellationToken ct);
}

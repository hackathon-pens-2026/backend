namespace SignIt.Modules.Workflow.Services;

public sealed class WorkflowOptions
{
    // SLA default per tugas aktif; DueAt dipakai reminder dan indikator overdue.
    public int SlaDays { get; set; } = 3;
}

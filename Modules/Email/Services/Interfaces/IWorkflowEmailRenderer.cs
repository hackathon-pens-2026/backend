using SignIt.Modules.Letters.Models;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Email.Services;

public interface IWorkflowEmailRenderer
{
    (string Subject, string Text, string Html) RenderSubmitted(LetterRequest letter, string link);
    (string Subject, string Text, string Html) RenderTaskActive(WorkflowTask task, LetterRequest letter, string link);
    (string Subject, string Text, string Html) RenderRevisionRequested(LetterRequest letter, string comment, string link);
    (string Subject, string Text, string Html) RenderRejected(LetterRequest letter, string comment, string link);
    (string Subject, string Text, string Html) RenderCompleted(LetterRequest letter, string link);
    (string Subject, string Text, string Html) RenderReminder(LetterRequest letter, string link);
    (string Subject, string Text, string Html) RenderGeneric(string subject, string message, string link);
}

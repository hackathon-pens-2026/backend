using System.Net;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Email.Services;

public sealed class WorkflowEmailRenderer : IWorkflowEmailRenderer
{
    public (string Subject, string Text, string Html) RenderSubmitted(LetterRequest letter, string link)
        => RenderGeneric("Surat berhasil diajukan",
            $"Surat {letter.Number} ({letter.Title}) telah diajukan dan menunggu persetujuan.", link);

    public (string Subject, string Text, string Html) RenderTaskActive(WorkflowTask task, LetterRequest letter, string link)
        => RenderGeneric($"Surat {letter.Number}: menunggu persetujuan Anda",
            $"Surat {letter.Number} ({letter.Title}) menunggu tindakan Anda.", link);

    public (string Subject, string Text, string Html) RenderRevisionRequested(LetterRequest letter, string comment, string link)
        => RenderGeneric($"Surat {letter.Number}: perlu revisi",
            $"Surat {letter.Number} memerlukan revisi. Catatan: {comment}", link);

    public (string Subject, string Text, string Html) RenderRejected(LetterRequest letter, string comment, string link)
        => RenderGeneric($"Surat {letter.Number}: ditolak",
            $"Surat {letter.Number} ditolak. Alasan: {comment}", link);

    public (string Subject, string Text, string Html) RenderCompleted(LetterRequest letter, string link)
        => RenderGeneric($"Surat {letter.Number}: selesai",
            $"Surat {letter.Number} ({letter.Title}) telah selesai dan dapat diunduh melalui aplikasi.", link);

    public (string Subject, string Text, string Html) RenderReminder(LetterRequest letter, string link)
        => RenderGeneric($"Pengingat: surat {letter.Number} menunggu tindakan Anda",
            $"Tugas Anda pada surat {letter.Number} telah melewati batas waktu. Mohon segera ditindaklanjuti.", link);

    public (string Subject, string Text, string Html) RenderGeneric(string subject, string message, string link)
    {
        var safeMessage = WebUtility.HtmlEncode(message);
        var safeLink = WebUtility.HtmlEncode(link);
        var text = $"{message}\nTinjau surat: {link}";
        var html = $"<p>{safeMessage}</p><p><a href=\"{safeLink}\">Tinjau surat</a></p>";
        return (subject, text, html);
    }
}

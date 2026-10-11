using PdfSharp.Pdf.IO;
using SignIt.Infrastructure.Errors;
using SignIt.Modules.Routing.Services;

namespace SignIt.Modules.Letters.Services;

public interface ISignatureSlotValidator
{
    void ValidateSlots(byte[] pdfBytes, SignatureSlot[] slots, IReadOnlyList<RoutingStage> stages);
}

public sealed class PdfSignatureSlotValidator : ISignatureSlotValidator
{
    public void ValidateSlots(byte[] pdfBytes, SignatureSlot[] slots, IReadOnlyList<RoutingStage> stages)
    {
        if (slots == null || slots.Length != stages.Count || slots.Select(x => x.PositionCode).Distinct().Count() != slots.Length
            || stages.Any(x => !slots.Any(s => s.PositionCode == x.PositionCode)))
        {
            throw new SignItDomainException(DomainErrorKind.Validation, "invalid_slots", "Sediakan satu slot untuk setiap tahap.");
        }

        using var stream = new MemoryStream(pdfBytes);
        using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

        foreach (var slot in slots)
        {
            if (slot.PageIndex < 0 || slot.PageIndex >= pdf.PageCount || !double.IsFinite(slot.X) || !double.IsFinite(slot.Y)
                || !double.IsFinite(slot.Width) || !double.IsFinite(slot.Height) || slot.X < 0 || slot.Y < 0 || slot.Width < 100 || slot.Height < 100)
            {
                throw new SignItDomainException(DomainErrorKind.Validation, "invalid_slot_bounds", "Slot QR tidak valid; ukuran minimal 100 × 100 point.");
            }

            var page = pdf.Pages[slot.PageIndex];
            if (page.Rotate != 0 || slot.X + slot.Width > page.Width.Point || slot.Y + slot.Height > page.Height.Point)
            {
                throw new SignItDomainException(DomainErrorKind.Validation, "invalid_slot_bounds", "Slot melewati halaman atau halaman perlu dinormalisasi rotasinya.");
            }

            if (slots.Any(other => other != slot && other.PageIndex == slot.PageIndex && slot.X < other.X + other.Width
                && slot.X + slot.Width > other.X && slot.Y < other.Y + other.Height && slot.Y + slot.Height > other.Y))
            {
                throw new SignItDomainException(DomainErrorKind.Validation, "overlapping_slots", "Slot tanda tangan bertumpuk.");
            }
        }
    }
}

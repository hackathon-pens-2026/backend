using SignIt.Modules.Routing.Services;

namespace SignIt.Modules.Letters.Services;

public interface ISignatureSlotValidator
{
    void ValidateSlots(byte[] pdfBytes, SignatureSlot[] slots, IReadOnlyList<RoutingStage> stages);
}

namespace SignIt.Modules.Signatures.Services;

public interface IQrCodeGenerator
{
    byte[] GeneratePng(string payload, int pixelsPerModule = 10);
    byte[] GenerateBmp(string payload, int pixelsPerModule = 10);
    string ComputeSha256(byte[] data);
}

namespace SignIt.Modules.Signatures.Models;

public enum SignatureQrStatus
{
    Active = 1,
    Rotated = 2,
    Revoked = 3
}

public enum VerificationStatus
{
    Valid = 1,
    Revoked = 2
}

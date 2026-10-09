using System.Text.Json.Serialization;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Modules.Signatures.DTOs;

public sealed record UserSignatureQrDto(
    Guid Id,
    Guid OwnerUserId,
    int Version,
    string ImageSha256,
    string QrDataUrl,
    string OpaqueCode,
    DateTimeOffset CreatedAt);

public sealed record SignTaskRequest(
    [property: JsonRequired] Guid ExpectedRevisionId,
    [property: JsonRequired] string ExpectedContentHash,
    string? Comment = null,
    [property: JsonRequired] Guid ExpectedTaskVersion = default);

public sealed record SignTaskResultDto(
    Guid TaskId,
    WorkflowTaskStatus Status,
    DateTimeOffset ActedAt,
    Guid EvidenceId,
    string Role,
    string? Position,
    string ContentHash,
    string QrSha256,
    bool IsWorkflowCompleted,
    string? VerificationCode);

public sealed record SignerSummaryDto(
    string Name,
    string Role,
    string? Position,
    DateTimeOffset SignedAt);

public sealed record PublicVerificationDto(
    string VerificationCode,
    string Status,
    string LetterNumber,
    string LetterTitle,
    string LetterType,
    DateTimeOffset PublishedAt,
    bool IsRevoked,
    DateTimeOffset? RevokedAt,
    string? RevocationReason,
    string FileSha256Fingerprint,
    IReadOnlyList<SignerSummaryDto> Signers);

public sealed record VerifyUploadResultDto(
    bool Matches,
    string? VerificationCode,
    PublicVerificationDto? Details,
    string Message);

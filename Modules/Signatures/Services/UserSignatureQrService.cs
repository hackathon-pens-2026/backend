using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Models;

namespace SignIt.Modules.Signatures.Services;

public sealed class UserSignatureQrService : IUserSignatureQrService
{
    private readonly AppDbContext _db;
    private readonly IQrCodeGenerator _qrGenerator;
    private readonly IStorageService _storage;
    private readonly TimeProvider _clock;
    private readonly ILogger<UserSignatureQrService> _logger;

    public UserSignatureQrService(
        AppDbContext db,
        IQrCodeGenerator qrGenerator,
        IStorageService storage,
        TimeProvider clock,
        ILogger<UserSignatureQrService> logger)
    {
        _db = db;
        _qrGenerator = qrGenerator;
        _storage = storage;
        _clock = clock;
        _logger = logger;
    }

    public async Task<UserSignatureQrDto> GetOrCreateForUserAsync(Guid userId, CancellationToken ct)
    {
        var entity = await EnsureActiveEntityAsync(userId, ct);
        var bytes = await _storage.ReadBytesAsync(entity.PrivateStorageKey, ct);
        if (bytes == null || bytes.Length == 0)
        {
            throw MissingAsset();
        }

        var dataUrl = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        return new UserSignatureQrDto(
            entity.Id,
            entity.OwnerUserId,
            entity.Version,
            entity.ImageSha256,
            dataUrl,
            entity.OpaqueCode,
            entity.CreatedAt);
    }

    public async Task<UserSignatureQrDto?> GetActiveByUserIdAsync(Guid userId, CancellationToken ct)
    {
        var entity = await _db.SignatureQrs
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerUserId == userId && x.Status == SignatureQrStatus.Active, ct);

        if (entity == null) return null;

        var bytes = await _storage.ReadBytesAsync(entity.PrivateStorageKey, ct);
        if (bytes == null || bytes.Length == 0)
        {
            throw MissingAsset();
        }

        var dataUrl = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        return new UserSignatureQrDto(
            entity.Id,
            entity.OwnerUserId,
            entity.Version,
            entity.ImageSha256,
            dataUrl,
            entity.OpaqueCode,
            entity.CreatedAt);
    }

    public async Task<(byte[] Bytes, string MimeType, string Sha256)?> GetRawImageAsync(Guid userId, CancellationToken ct)
    {
        var entity = await EnsureActiveEntityAsync(userId, ct);
        var bytes = await _storage.ReadBytesAsync(entity.PrivateStorageKey, ct);
        if (bytes == null || bytes.Length == 0)
        {
            throw MissingAsset();
        }

        return (bytes, "image/png", entity.ImageSha256);
    }

    public async Task<UserSignatureQr> EnsureActiveEntityAsync(Guid userId, CancellationToken ct)
    {
        var existing = await _db.SignatureQrs
            .SingleOrDefaultAsync(x => x.OwnerUserId == userId && x.Status == SignatureQrStatus.Active, ct);

        if (existing != null)
        {
            var image = await _storage.ReadBytesAsync(existing.PrivateStorageKey, ct);
            if (image == null || !string.Equals(_qrGenerator.ComputeSha256(image), existing.ImageSha256, StringComparison.OrdinalIgnoreCase))
                throw MissingAsset();
            return existing;
        }

        var user = await _db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user == null)
            throw new SignItDomainException(DomainErrorKind.NotFound, "user_not_found", "Pengguna tidak ditemukan.");

        var opaqueCode = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var payload = $"signit:sig:{opaqueCode}";
        var qrBytes = _qrGenerator.GeneratePng(payload, pixelsPerModule: 12);
        var hash = _qrGenerator.ComputeSha256(qrBytes);
        var storageKey = $"signatures/users/{userId}/{opaqueCode}/qr-v1.png";
        var now = _clock.GetUtcNow();

        await _storage.SaveAsync(storageKey, qrBytes, "image/png", ct);

        var qr = UserSignatureQr.Create(
            Guid.NewGuid(),
            userId,
            opaqueCode,
            storageKey,
            version: 1,
            imageSha256: hash,
            now);

        _db.SignatureQrs.Add(qr);

        try
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Generated user signature QR for User {UserId}.", userId);
            return qr;
        }
        catch (DbUpdateException)
        {
            // In case of concurrent race condition, return the existing active entity
            _db.Entry(qr).State = EntityState.Detached;
            var concurrent = await _db.SignatureQrs
                .SingleOrDefaultAsync(x => x.OwnerUserId == userId && x.Status == SignatureQrStatus.Active, ct);
            if (concurrent != null) return concurrent;
            throw;
        }
    }

    private static SignItDomainException MissingAsset() => new(
        DomainErrorKind.ProcessingFailed, "signature_asset_invalid",
        "Aset QR tidak tersedia atau rusak. Hubungi tim untuk pemulihan aset tanda tangan.");
}

using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Authentication.DTOs;
using SignIt.Modules.Authentication.Models;
using Xunit;

namespace SignIt.Auth.Tests;

public sealed class AuthDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static User CreateUser(UserCategory category = UserCategory.StudentGeneral)
        => User.Provision(Guid.NewGuid(), "Mahasiswa", "student@signit.example", null, category, "stored-hash", Now, false);

    [Theory]
    [InlineData(UserCategory.StudentGeneral, UiSurface.Student)]
    [InlineData(UserCategory.StudentDagri, UiSurface.Student)]
    [InlineData(UserCategory.BAAK, UiSurface.Management)]
    [InlineData(UserCategory.Management, UiSurface.Management)]
    public void FourCategoriesMapToOnlyTwoSurfaces(UserCategory category, UiSurface expected)
        => Assert.Equal(expected, CreateUser(category).Surface);

    [Fact]
    public void LockoutBlocksEvenSuccessfulLoginUntilItsBoundary()
    {
        var user = CreateUser();
        for (var i = 0; i < 5; i++) user.RecordFailedLogin(Now, 5, TimeSpan.FromMinutes(15));
        Assert.False(user.CanLogin(Now.AddMinutes(14)));
        Assert.Throws<InvalidOperationException>(() => user.RecordSuccessfulLogin(Now, null));
        Assert.True(user.CanLogin(Now.AddMinutes(15)));
        user.RecordSuccessfulLogin(Now.AddMinutes(15), null);
        Assert.Null(user.LockedUntil);
        Assert.Equal(0, user.FailedLoginCount);
    }

    [Fact]
    public void PasswordResetInvalidatesExistingSessionsAndResetTokensViaSecurityStamp()
    {
        var user = CreateUser();
        var session = AuthSession.Create(user, Now, Now.AddDays(7));
        var reset = PasswordResetToken.Create(user, "token-hash", Now, Now.AddMinutes(30));
        var stamp = user.SecurityStamp;
        user.ResetPassword("new-hash");
        Assert.NotEqual(stamp, user.SecurityStamp);
        Assert.False(session.IsValid(user, Now));
        Assert.False(reset.IsValid(user, Now));
    }

    [Fact]
    public void RevokedSessionRemainsRevokedAndDoesNotChangeItsOriginalReason()
    {
        var user = CreateUser();
        var session = AuthSession.Create(user, Now, Now.AddDays(7));
        session.Revoke(Now, "logout");
        session.Revoke(Now.AddMinutes(1), "refresh_token_reuse");
        Assert.False(session.IsValid(user, Now));
        Assert.Equal("logout", session.RevocationReason);
        Assert.Equal(Now, session.RevokedAt);
    }

    [Fact]
    public void SessionsAndResetTokensRejectExactExpiryInstant()
    {
        var user = CreateUser();
        var expiry = Now.AddMinutes(30);
        Assert.False(AuthSession.Create(user, Now, expiry).IsValid(user, expiry));
        Assert.False(PasswordResetToken.Create(user, "hash", Now, expiry).IsValid(user, expiry));
    }

    [Fact]
    public void ConsumedRefreshCannotBeReused()
    {
        var refresh = RefreshCredential.Create(Guid.NewGuid(), "hash", Now, Now.AddDays(7));
        refresh.Consume(Now);
        Assert.Throws<InvalidOperationException>(() => refresh.Consume(Now.AddMinutes(1)));
    }

    [Fact]
    public void ConsumedResetCannotBeUsedAgain()
    {
        var user = CreateUser();
        var reset = PasswordResetToken.Create(user, "hash", Now, Now.AddMinutes(30));
        reset.Invalidate(Now);
        Assert.False(reset.IsValid(user, Now));
    }

    [Fact]
    public void AssignmentCannotHaveEmptyScopeOrZeroLengthTenure()
    {
        Assert.Throws<ArgumentException>(() => UserAssignment.Provision(Guid.NewGuid(), Guid.NewGuid(),
            "BAAK", "BAAK", "", UserCapability.Approver, Now, null));
        Assert.Throws<ArgumentException>(() => UserAssignment.Provision(Guid.NewGuid(), Guid.NewGuid(),
            "BAAK", "BAAK", "unit:baak", UserCapability.Approver, Now, Now));
    }

    [Fact]
    public void PasswordPolicySupportsLongPassphrasesButRejectsShortOrUnboundedInput()
    {
        var options = new AuthOptions();
        PasswordPolicy.Validate("contoh passphrase yang panjang", options);
        Assert.Throws<AuthException>(() => PasswordPolicy.Validate("pendek", options));
        Assert.Throws<AuthException>(() => PasswordPolicy.Validate(new string('a', 129), options));
    }

    [Fact]
    public void DailyBudgetResetsTomorrowWhileMonthlyBudgetDoesNot()
    {
        var budget = ResetEmailBudget.Create();
        Assert.Null(budget.Reserve(Now, 1, 2));
        Assert.Equal(Now.AddDays(1), budget.Reserve(Now, 1, 2));
        Assert.Null(budget.Reserve(Now.AddDays(1), 1, 2));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), budget.Reserve(Now.AddDays(2), 1, 2));
    }

    [Fact]
    public void AcceptedEmailClearsSensitivePayloadWithoutClaimingDelivery()
    {
        var email = PasswordResetEmail.Queue(Guid.NewGuid(), "student@signit.example", "protected-token", Now);
        email.BeginAttempt(Now, TimeSpan.FromMinutes(2));
        email.Accept("provider-message-id", Now);
        Assert.Equal(ResetEmailStatus.Accepted, email.Status);
        Assert.Empty(email.ProtectedPayload);
    }

    [Fact]
    public void ExhaustedAmbiguousSendStaysUnknownAndCannotBeRetriedBlindly()
    {
        var email = PasswordResetEmail.Queue(Guid.NewGuid(), "student@signit.example", "protected-token", Now);
        email.BeginAttempt(Now, TimeSpan.FromMinutes(2));
        email.Stop("retry_requires_reconciliation", unknown: true);
        Assert.Equal(ResetEmailStatus.Unknown, email.Status);
        Assert.Equal(DateTimeOffset.MaxValue, email.NextAttemptAt);
        Assert.Empty(email.ProtectedPayload);
    }
}

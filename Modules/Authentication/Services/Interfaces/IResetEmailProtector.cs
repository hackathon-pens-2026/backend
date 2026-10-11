namespace SignIt.Modules.Authentication.Services;

public interface IResetEmailProtector
{
    string Protect(string rawToken);
    string Unprotect(string protectedToken);
}

namespace SignIt.Modules.Authentication.Services;

public enum PasswordCheck { Failed, Success, RehashNeeded }

public interface IPasswordService
{
    string Hash(string password);
    PasswordCheck Verify(string hash, string password);
    void VerifyDummy(string password);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Persistence;

namespace SignIt.Modules.Authentication.Data;

// Local fixtures only; assignments are not a declaration of real campus authority.
public static class DemoAccountProvisioner
{
    public static async Task<int> ProvisionAsync(AppDbContext db, AuthProvisioner provisioner,
        string contentRoot, CancellationToken ct)
    {
        var organizations = await db.Organizations.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Scope).ToListAsync(ct);
        if (organizations.Count != 12)
            throw new InvalidOperationException("Seed 12 organisasi harus tersedia sebelum membuat akun demo.");
        var directory = Path.Combine(contentRoot, ".data", "demo");
        Directory.CreateDirectory(directory);
        var credentialsPath = Path.Combine(directory, "credentials.json");
        var credentials = File.Exists(credentialsPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(credentialsPath, ct))!
            : new Dictionary<string, string>();
        var users = new List<object>();
        var variables = new List<string>();
        object Assignment(string account, string scope, string position, string name, string capability) => new
        {
            id = StableId($"assignment:{account}:{scope}:{position}:{capability}"),
            positionCode = position, positionName = name, scope, capability,
            validFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        void Account(string key, string name, string category, IEnumerable<object> assignments)
        {
            var email = $"{key}@demo.signit.example";
            if (!credentials.ContainsKey(email)) credentials[email] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            var variable = "SIGNIT_DEMO_" + StableId(key).ToString("N");
            Environment.SetEnvironmentVariable(variable, credentials[email]);
            variables.Add(variable);
            users.Add(new { id = StableId("user:" + key), name, email, category,
                passwordEnvironmentVariable = variable, emailVerified = true, assignments = assignments.ToArray() });
        }
        foreach (var org in organizations)
            Account("pengaju-" + org.Scope, "Demo Pengaju " + org.Name, "StudentGeneral",
                [Assignment("pengaju-" + org.Scope, org.Scope, "Pengaju", "Pengaju", "Requester")]);
        var roles = new[]
        {
            ("ketupel", "Ketupel", "Ketua Pelaksana", "StudentGeneral", "Signer"),
            ("ketua", "KetuaOrganisasi", "Ketua Organisasi", "StudentGeneral", "Signer"),
            ("pembina", "Pembina", "Pembina Organisasi", "Management", "Approver"),
            ("kemahasiswaan", "Kemahasiswaan", "Kemahasiswaan", "Management", "Approver"),
            ("minat-bakat", "MinatBakat", "Tim Pembina Minat dan Bakat", "Management", "Approver"),
            ("dagri", "Dagri", "Dagri BEM", "StudentDagri", "Approver"),
            ("baak", "BAAK", "BAAK", "BAAK", "Approver"),
            ("wadir3", "Wadir3", "Wakil Direktur III", "Management", "Approver"),
            ("wadir2", "Wadir2", "Wakil Direktur II", "Management", "Approver")
        };
        foreach (var (key, code, name, category, capability) in roles)
            Account(key, "Demo " + name, category, organizations
                .Where(org => code != "Kemahasiswaan" || org.Kind == "Himpunan")
                .Where(org => code != "MinatBakat" || org.Kind == "Organisasi")
                .Select(org => Assignment(key, org.Scope, code, name, capability)));
        var manifest = Path.Combine(directory, "manifest.json");
        var options = new JsonSerializerOptions { WriteIndented = true };
        try
        {
            // Persist before provisioning so a retry can recover the initial credentials.
            await File.WriteAllTextAsync(credentialsPath, JsonSerializer.Serialize(credentials, options), ct);
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { users }, options), ct);
            return await provisioner.ProvisionAsync(manifest, ct);
        }
        finally
        {
            foreach (var variable in variables) Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static Guid StableId(string value)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes("signit:local-demo:v1:" + value)).AsSpan(0, 16));
}

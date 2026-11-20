using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SceneryAtlas.Core;

public sealed class LocalAccounts(string directory)
{
    private const int Iterations = 600_000;
    private readonly string path = Path.Combine(directory, "accounts.json");
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> attempts = new(StringComparer.OrdinalIgnoreCase);
    public LocalUser? Session { get; private set; }
    public record Account(string Id, string Username, string DisplayName, string Salt, string Hash, int Iterations);
    private List<Account> Load() => Json.Read<List<Account>>(path, []);

    public LocalUser Register(string username, string displayName, string password)
    {
        username = username.Trim().ToLowerInvariant(); displayName = displayName.Trim();
        if (!Regex.IsMatch(username, "^[a-z0-9][a-z0-9_.-]{2,31}$")) throw new ArgumentException("Use a username of 3–32 letters, numbers, dots, dashes or underscores.");
        if (displayName.Length is < 1 or > 60) throw new ArgumentException("Enter a display name of 1–60 characters.");
        if (password.Length is < 12 or > 256) throw new ArgumentException("Use a password of 12–256 characters.");
        var accounts = Load();
        if (accounts.Any(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That local username already exists.");
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var account = new Account(Guid.NewGuid().ToString("N"), username, displayName, Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations);
        accounts.Add(account); Json.Save(path, accounts);
        return Session = new(account.Id, account.Username, account.DisplayName);
    }

    public LocalUser Login(string username, string password)
    {
        username = username.Trim().ToLowerInvariant();
        if (username.Length > 32 || password.Length > 256) throw new ArgumentException("Invalid username or password.");
        if (attempts.TryGetValue(username, out var prior) && prior.Until > DateTimeOffset.UtcNow) throw new ArgumentException("Too many attempts. Please wait a minute.");
        var account = Load().FirstOrDefault(a => a.Username == username);
        var salt = account is null ? new byte[32] : Convert.FromBase64String(account.Salt);
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, account?.Iterations ?? Iterations, HashAlgorithmName.SHA256, 32);
        var expected = account is null ? new byte[32] : Convert.FromBase64String(account.Hash);
        if (account is null || !CryptographicOperations.FixedTimeEquals(derived, expected))
        {
            var count = prior.Count + 1; attempts[username] = (count, count >= 5 ? DateTimeOffset.UtcNow.AddMinutes(1) : DateTimeOffset.MinValue);
            throw new ArgumentException("Invalid username or password.");
        }
        attempts.Remove(username);
        return Session = new(account.Id, account.Username, account.DisplayName);
    }
    public void Logout() => Session = null;
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiImageClient;

public class UiAuthSecurityTests
{
    [Fact]
    public void VersionTwoHashAuthenticatesAndProducesRevocableCookie()
    {
        WithAuthFile(
            BuildVersionTwo("alice", "correct horse battery staple"),
            auth =>
            {
                Assert.True(auth.TryLogin(
                    "alice",
                    "correct horse battery staple",
                    "127.0.0.1",
                    out var cookie,
                    out var error));
                Assert.Equal("", error);
                Assert.True(auth.TryValidateCookie(cookie, out var username));
                Assert.Equal("alice", username);
                Assert.False(auth.TryLogin(
                    "alice",
                    "wrong",
                    "127.0.0.2",
                    out _,
                    out _));
            });
    }

    [Fact]
    public void CookieDiesWhenPasswordHashChanges()
    {
        var first = BuildVersionTwo("alice", "correct horse battery staple");
        var second = BuildVersionTwo("alice", "a different passphrase");
        string cookie = "";
        WithAuthFile(first, auth =>
        {
            Assert.True(auth.TryLogin(
                "alice",
                "correct horse battery staple",
                "127.0.0.1",
                out cookie,
                out _));
        });
        WithAuthFile(second, auth =>
        {
            Assert.False(auth.TryValidateCookie(cookie, out _));
            Assert.True(auth.TryLogin(
                "alice",
                "a different passphrase",
                "127.0.0.1",
                out var next,
                out _));
            Assert.True(auth.TryValidateCookie(next, out var username));
            Assert.Equal("alice", username);
        });
    }

    [Fact]
    public void CookieDiesWhenSecretChanges()
    {
        var first = BuildVersionTwo("alice", "correct horse battery staple", "0123456789abcdef0123456789abcdef");
        var second = BuildVersionTwo("alice", "correct horse battery staple", "ffffffffffffffffffffffffffffffff");
        string cookie = "";
        WithAuthFile(first, auth =>
        {
            Assert.True(auth.TryLogin(
                "alice",
                "correct horse battery staple",
                "127.0.0.1",
                out cookie,
                out _));
        });
        WithAuthFile(second, auth =>
        {
            Assert.False(auth.TryValidateCookie(cookie, out _));
        });
    }

    [Fact]
    public void PlaintextVersionOneFileFailsClosed()
    {
        const string json = """
            {
              "enabled": true,
              "secret": "0123456789abcdef0123456789abcdef",
              "accounts": [
                { "username": "alice", "password": "plaintext" }
              ]
            }
            """;

        AssertAuthFileRejected(json);
    }

    [Fact]
    public void ConfiguredDisabledFileFailsClosed()
    {
        var json = BuildVersionTwo("alice", "password").Replace(
            "\"enabled\":true",
            "\"enabled\":false",
            StringComparison.Ordinal);

        AssertAuthFileRejected(json);
    }

    [Fact]
    public void UnknownAuthFieldsFailClosed()
    {
        var json = BuildVersionTwo("alice", "password").Replace(
            "\"version\":2",
            "\"version\":2,\"unexpected\":true",
            StringComparison.Ordinal);

        AssertAuthFileRejected(json);
    }

    [Fact]
    public void DuplicateAuthFieldsFailClosed()
    {
        var json = BuildVersionTwo("alice", "password").Replace(
            "\"version\":2",
            "\"version\":2,\"version\":2",
            StringComparison.Ordinal);

        AssertAuthFileRejected(json);
    }

    [Fact]
    public void WrongCaseAuthFieldsFailClosed()
    {
        var json = BuildVersionTwo("alice", "password").Replace(
            "\"version\":2",
            "\"Version\":2",
            StringComparison.Ordinal);

        AssertAuthFileRejected(json);
    }

    [Fact]
    public void WeakHashIterationCountFailsClosed()
    {
        var json = BuildVersionTwo("alice", "password").Replace(
            "$600000$",
            "$1$",
            StringComparison.Ordinal);

        AssertAuthFileRejected(json);
    }

    private static string BuildVersionTwo(
        string username,
        string password,
        string secret = "0123456789abcdef0123456789abcdef")
    {
        var salt = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            UiAuth.Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            UiAuth.Pbkdf2HashBytes);
        return JsonSerializer.Serialize(new
        {
            version = 2,
            enabled = true,
            secret,
            accounts = new[]
            {
                new
                {
                    username,
                    passwordHash = $"{UiAuth.PasswordHashPrefix}${UiAuth.Pbkdf2Iterations}$"
                        + Convert.ToBase64String(salt)
                        + "$"
                        + Convert.ToBase64String(hash),
                },
            },
        });
    }

    private static void AssertAuthFileRejected(string json)
    {
        var path = WriteTemporaryAuthFile(json);
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => UiAuth.CreateFromSettings(new Settings { UiAuthFilePath = path }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WithAuthFile(string json, Action<UiAuth> assertion)
    {
        var path = WriteTemporaryAuthFile(json);
        try
        {
            var auth = UiAuth.CreateFromSettings(
                new Settings { UiAuthFilePath = path });
            Assert.NotNull(auth);
            assertion(auth);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTemporaryAuthFile(string json)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"multi-image-client-auth-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json, Encoding.UTF8);
        return path;
    }
}

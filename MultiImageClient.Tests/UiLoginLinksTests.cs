using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiImageClient;

namespace MultiImageClient.Tests;

public sealed class UiLoginLinksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mic-links-tests-" + Guid.NewGuid().ToString("N"));
    private const string SigningSecret = "0123456789abcdef0123456789abcdef";
    public UiLoginLinksTests() => Directory.CreateDirectory(_root);

    private UiLoginLinks Store(string name = "links")
    {
        var path = Path.Combine(_root, name + ".json");
        File.WriteAllText(path, "{\"version\":1,\"accounts\":[],\"defaultGenerators\":[]}");
        return new UiLoginLinks(path);
    }

    [Fact]
    public void ReusableLinkPreservesIdentityAndNeverPersistsItsSecret()
    {
        var store = Store();
        var issued = store.Create("Alice", _ => { });
        Assert.StartsWith("member-", issued.Account.Login);
        Assert.DoesNotContain(issued.Token.Split('.')[1], File.ReadAllText(Path.Combine(_root, "links.json")));
        for (var i = 0; i < 2; i++)
        {
            Assert.True(store.TryExchange(issued.Token, SigningSecret, out var cookie, out var account));
            Assert.Equal(issued.Account.Login, account!.Login);
            Assert.True(store.TryValidateCookie(cookie, SigningSecret, out var login));
            Assert.Equal(issued.Account.Login, login);
        }
    }

    [Fact]
    public void ReplacementAndRevocationInvalidateExistingSessions()
    {
        var store = Store();
        var issued = store.Create("Alice", _ => { });
        Assert.True(store.TryExchange(issued.Token, SigningSecret, out var oldCookie, out _));
        var replacement = store.Replace(issued.Account.Id);
        Assert.False(store.TryExchange(issued.Token, SigningSecret, out _, out _));
        Assert.False(store.TryValidateCookie(oldCookie, SigningSecret, out _));
        Assert.True(store.TryExchange(replacement, SigningSecret, out var newCookie, out _));
        store.Revoke(issued.Account.Id);
        Assert.False(store.TryExchange(replacement, SigningSecret, out _, out _));
        Assert.False(store.TryValidateCookie(newCookie, SigningSecret, out _));
    }

    [Fact]
    public void AnotherEnvironmentRejectsLinksAndCookiesEvenWithTheSameSigningKey()
    {
        var first = Store("first"); var second = Store("second");
        var issued = first.Create("Alice", _ => { });
        second.Create("Alice", _ => { });
        Assert.True(first.TryExchange(issued.Token, SigningSecret, out var cookie, out _));
        Assert.False(second.TryExchange(issued.Token, SigningSecret, out _, out _));
        Assert.False(second.TryValidateCookie(cookie, SigningSecret, out _));
        Assert.False(first.TryValidateCookie(cookie, "another-signing-secret", out _));
    }

    [Fact]
    public void DisplayNameCannotGrantOwnerPrivileges()
    {
        var store = Store(); var issued = store.Create(UiLoginLinks.OwnerLogin, _ => { });
        Assert.NotEqual(UiLoginLinks.OwnerLogin, issued.Account.Login);
        Assert.True(store.TryExchange(issued.Token, SigningSecret, out var cookie, out _));
        Assert.True(store.TryValidateCookie(cookie, SigningSecret, out var login));
        Assert.NotEqual(UiLoginLinks.OwnerLogin, login);
    }

    [Fact]
    public void FailedProfileCreationDoesNotIssueAnAccount()
    {
        var store = Store();
        Assert.Throws<InvalidOperationException>(() => store.Create("Alice", _ => throw new InvalidOperationException()));
        Assert.Empty(store.List());
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1,\"accounts\":[]}")]
    [InlineData("{\"version\":2,\"accounts\":[]}")]
    [InlineData("{\"version\":1,\"accounts\":[],\"unknown\":true}")]
    public void CorruptStateFailsClosed(string document)
    {
        var store = Store(); File.WriteAllText(Path.Combine(_root, "links.json"), document);
        Assert.Throws<InvalidDataException>(() => store.List());
    }

    [Fact]
    public void NamedEnvironmentUsesSeparateCookieNameAndPathAndKeepsPasswordLogin()
    {
        Store();
        var salt = RandomNumberGenerator.GetBytes(16);
        var digest = Rfc2898DeriveBytes.Pbkdf2("test-password", salt, 600000, HashAlgorithmName.SHA256, 32);
        var authPath = Path.Combine(_root, "auth.json");
        File.WriteAllText(authPath, JsonSerializer.Serialize(new { version = 2, enabled = true, secret = SigningSecret,
            accounts = new[] { new { username = UiLoginLinks.OwnerLogin,
                passwordHash = "pbkdf2-sha256$600000$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(digest) } } }));
        var settings = new Settings { UiAuthFilePath = authPath, UiEnvironmentId = "studio", UiEnvironmentName = "Studio",
            UiPublicBaseUrl = "https://example.test/new-studio", UiLoginLinksFilePath = Path.Combine(_root, "links.json") };
        var auth = UiAuth.CreateFromSettings(settings)!;
        Assert.Equal("mic_auth_studio", auth.SessionCookieName);
        Assert.Equal("/new-studio/", auth.SessionCookiePath);
        Assert.True(auth.TryLogin(UiLoginLinks.OwnerLogin, "test-password", "test", out var passwordCookie, out _));
        Assert.True(auth.TryValidateCookie(passwordCookie, out _));
        var issued = auth.LoginLinks!.Create("Alice", _ => { });
        Assert.True(auth.TryLoginLink(issued.Token, "test", out var linkCookie, out _, out _));
        Assert.True(auth.TryValidateCookie(linkCookie, out var user));
        Assert.Equal(issued.Account.Login, user);
        var legacy = UiAuth.CreateFromSettings(new Settings { UiAuthFilePath = authPath })!;
        Assert.Equal("mic_auth", legacy.SessionCookieName);
        Assert.Equal("/", legacy.SessionCookiePath);
        Assert.False(legacy.TryValidateCookie(linkCookie, out _));
    }

    public void Dispose() => Directory.Delete(_root, true);
}

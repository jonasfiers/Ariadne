using Ariadne.Core.Connection;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Connection;

/// <summary>
/// Unit tests for <see cref="AuthTokenBuilder"/> (spec §5): each scheme maps to the correct
/// <see cref="AuthTokens"/> factory, and an unknown/out-of-scope scheme fails loud. The driver does not
/// expose the credential contents on <see cref="IAuthToken"/>, so equality is asserted structurally against
/// the driver's own factory output (the driver builds an <c>AuthToken</c> whose <c>Equals</c> compares the
/// underlying dictionary).
/// </summary>
public class AuthTokenBuilderTests
{
    [Fact]
    public void Basic_scheme_builds_basic_token()
    {
        var config = new ConnConfig { AuthScheme = "Basic", Username = "neo4j", Password = "secret" };

        IAuthToken token = AuthTokenBuilder.BuildAuthToken(config);

        Assert.Equal(AuthTokens.Basic("neo4j", "secret"), token);
    }

    [Fact]
    public void Basic_scheme_is_case_insensitive()
    {
        var config = new ConnConfig { AuthScheme = "bAsIc", Username = "u", Password = "p" };

        IAuthToken token = AuthTokenBuilder.BuildAuthToken(config);

        Assert.Equal(AuthTokens.Basic("u", "p"), token);
    }

    [Fact]
    public void Bearer_scheme_builds_bearer_token()
    {
        var config = new ConnConfig { AuthScheme = "Bearer", BearerToken = "eyJ.tok.en" };

        IAuthToken token = AuthTokenBuilder.BuildAuthToken(config);

        Assert.Equal(AuthTokens.Bearer("eyJ.tok.en"), token);
    }

    [Fact]
    public void None_scheme_builds_none_token()
    {
        var config = new ConnConfig { AuthScheme = "None" };

        IAuthToken token = AuthTokenBuilder.BuildAuthToken(config);

        Assert.Equal(AuthTokens.None, token);
    }

    [Fact]
    public void Default_config_scheme_is_basic()
    {
        // A freshly-constructed config defaults to Basic (spec §5 default).
        var config = new ConnConfig { Username = "u", Password = "p" };

        IAuthToken token = AuthTokenBuilder.BuildAuthToken(config);

        Assert.Equal(AuthTokens.Basic("u", "p"), token);
    }

    [Theory]
    [InlineData("Kerberos")]
    [InlineData("Custom")]
    [InlineData("OIDC")]
    [InlineData("")]
    [InlineData("   ")]
    public void Unknown_or_out_of_scope_scheme_fails_loud(string scheme)
    {
        var config = new ConnConfig { AuthScheme = scheme };

        var ex = Assert.Throws<ConnectionException>(() => AuthTokenBuilder.BuildAuthToken(config));
        Assert.Contains("scheme", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Null_config_throws_argument_null()
    {
        Assert.Throws<System.ArgumentNullException>(() => AuthTokenBuilder.BuildAuthToken(null!));
    }

    [Fact]
    public void Exception_message_never_leaks_the_secret()
    {
        var config = new ConnConfig { AuthScheme = "Kerberos", Password = "topsecret", BearerToken = "tok-abc" };

        var ex = Assert.Throws<ConnectionException>(() => AuthTokenBuilder.BuildAuthToken(config));
        Assert.DoesNotContain("topsecret", ex.Message);
        Assert.DoesNotContain("tok-abc", ex.Message);
    }
}

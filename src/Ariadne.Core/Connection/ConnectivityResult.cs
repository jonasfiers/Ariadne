namespace Ariadne.Core.Connection;

/// <summary>
/// The outcome of a <see cref="ConnectivityVerifier.VerifyConnectivity(ConnConfig)"/> check (spec §10) — a
/// "test connection" result for setup screens and the demo. Either <see cref="Ok"/> is <c>true</c> (the
/// server was reachable and accepted the driver's handshake), or it is <c>false</c> and
/// <see cref="ErrorType"/>/<see cref="ErrorMessage"/> carry a typed description of the failure — a returned
/// value, never an unhandled throw.
/// </summary>
/// <remarks>
/// This is a deliberately minimal shape. The full driver-exception → named-OutSystems-error mapping table
/// (spec §9) is Feature 09; here the failure carries only the driver exception's type name and message,
/// which — for a connectivity check — never contains a credential.
/// </remarks>
public sealed class ConnectivityResult
{
    private ConnectivityResult(bool ok, string? errorType, string? errorMessage)
    {
        Ok = ok;
        ErrorType = errorType;
        ErrorMessage = errorMessage;
    }

    /// <summary>Whether connectivity was verified successfully.</summary>
    public bool Ok { get; }

    /// <summary>The failing exception's type name when <see cref="Ok"/> is <c>false</c>; otherwise null.</summary>
    public string? ErrorType { get; }

    /// <summary>The failing exception's message when <see cref="Ok"/> is <c>false</c>; otherwise null.</summary>
    public string? ErrorMessage { get; }

    /// <summary>The success result.</summary>
    public static ConnectivityResult Success { get; } = new ConnectivityResult(true, null, null);

    /// <summary>Builds a failure result carrying the exception's type name and message.</summary>
    /// <param name="ex">The exception the connectivity check surfaced.</param>
    /// <returns>A non-ok result.</returns>
    public static ConnectivityResult Failure(System.Exception ex)
    {
        return new ConnectivityResult(false, ex?.GetType().Name, ex?.Message);
    }
}

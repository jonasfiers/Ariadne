using Neo4j.Driver;

namespace Ariadne.Core.Connection;

/// <summary>
/// The production <see cref="IDriverFactory"/>: constructs a real driver via
/// <see cref="GraphDatabase.Driver(string, IAuthToken, System.Action{ConfigBuilder})"/>. Only the four
/// tuning knobs that carry a non-null value are applied; every null knob is left at the driver's own
/// default (spec §7). Encryption/TLS is intentionally <b>not</b> configured here — it is carried by the
/// URI scheme (spec §6), and setting it in both places makes the 5.x driver throw.
/// </summary>
/// <remarks>
/// This type is deliberately thin and is exercised end-to-end only against a live server (CI oracle); the
/// cache/auth/connectivity logic it feeds is fully covered by fakes. Verified against Neo4j.Driver 5.28.3:
/// the overload <c>Driver(string, IAuthToken, Action&lt;ConfigBuilder&gt;)</c> exists, and the
/// <c>ConfigBuilder</c> exposes <c>WithMaxConnectionPoolSize(int)</c>,
/// <c>WithConnectionAcquisitionTimeout(TimeSpan)</c>, <c>WithMaxTransactionRetryTime(TimeSpan)</c>, and
/// <c>WithFetchSize(long)</c>.
/// </remarks>
public sealed class GraphDatabaseDriverFactory : IDriverFactory
{
    /// <inheritdoc />
    public IDriver Create(ConnConfig config)
    {
        IAuthToken auth = AuthTokenBuilder.BuildAuthToken(config);

        return GraphDatabase.Driver(config.Uri, auth, o =>
        {
            if (config.MaxConnectionPoolSize.HasValue)
            {
                o.WithMaxConnectionPoolSize(config.MaxConnectionPoolSize.Value);
            }

            if (config.ConnectionAcquisitionTimeout.HasValue)
            {
                o.WithConnectionAcquisitionTimeout(config.ConnectionAcquisitionTimeout.Value);
            }

            if (config.MaxTransactionRetryTime.HasValue)
            {
                o.WithMaxTransactionRetryTime(config.MaxTransactionRetryTime.Value);
            }

            if (config.FetchSize.HasValue)
            {
                o.WithFetchSize(config.FetchSize.Value);
            }
        });
    }
}

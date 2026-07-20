using Neo4j.Driver;

namespace Ariadne.Core.Connection;

/// <summary>
/// The seam that makes the connection layer unit-testable without a live Neo4j. Production code uses
/// <see cref="GraphDatabaseDriverFactory"/> (which calls the real
/// <c>GraphDatabase.Driver(...)</c>); tests inject a hand-rolled fake returning a fake
/// <see cref="IDriver"/>, so <see cref="DriverCache"/> and connectivity checks are exercised with zero
/// network traffic.
/// </summary>
/// <remarks>
/// Each <see cref="Create(ConnConfig)"/> call constructs a <b>new</b> heavyweight <see cref="IDriver"/>
/// (which owns a connection pool). It is <see cref="DriverCache"/>'s job — not the factory's — to ensure
/// this is called at most once per connection identity. A factory implementation must not itself cache.
/// </remarks>
public interface IDriverFactory
{
    /// <summary>
    /// Builds a fresh <see cref="IDriver"/> for the given configuration (auth token from
    /// <see cref="AuthTokenBuilder"/>, driver tuning from the §7 knobs). Heavyweight — see the type remarks.
    /// </summary>
    /// <param name="config">The connection configuration.</param>
    /// <returns>A newly constructed driver singleton candidate.</returns>
    IDriver Create(ConnConfig config);
}

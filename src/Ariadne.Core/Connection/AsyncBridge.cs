using System.Threading.Tasks;

namespace Ariadne.Core.Connection;

/// <summary>
/// The sync-over-async bridge (connection spec §2). OutSystems Integration Studio actions are synchronous;
/// the modern <c>Neo4j.Driver</c> (5.x) is async-first. This helper blocks on an async operation at the
/// outermost boundary <b>correctly</b>.
/// </summary>
/// <remarks>
/// <para>
/// It uses <c>.ConfigureAwait(false).GetAwaiter().GetResult()</c> — deliberately <b>not</b> <c>.Result</c>
/// or <c>.Wait()</c>, which wrap the real fault in an <see cref="System.AggregateException"/>. When the
/// awaited task faults, <c>GetAwaiter().GetResult()</c> re-throws the <b>original</b> exception (e.g. a
/// driver <c>ServiceUnavailableException</c>), which the Feature 09 error-mapping table depends on.
/// <c>ConfigureAwait(false)</c> avoids capturing a synchronization context (defensive — there is none in an
/// extension, but it keeps the block deadlock-free anywhere).
/// </para>
/// <para>
/// Keep the blocking at the outermost call (one <c>RunSync</c> per action), never sprinkled through inner
/// async code.
/// </para>
/// </remarks>
public static class AsyncBridge
{
    /// <summary>
    /// Synchronously waits for a non-returning async operation, re-throwing the original exception (not an
    /// <see cref="System.AggregateException"/>) if it faults.
    /// </summary>
    /// <param name="task">The task to block on.</param>
    public static void RunSync(Task task)
    {
        task.ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Synchronously waits for an async operation and returns its result, re-throwing the original exception
    /// (not an <see cref="System.AggregateException"/>) if it faults.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="task">The task to block on.</param>
    /// <returns>The task's result.</returns>
    public static T RunSync<T>(Task<T> task)
    {
        return task.ConfigureAwait(false).GetAwaiter().GetResult();
    }
}

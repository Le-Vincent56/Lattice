using System.Threading;
using System.Threading.Tasks;

namespace Didionysymus.Lattice.Runtime
{
    /// <summary>
    /// Defines a contract for objects that can be started asynchronously,
    /// allowing initialization logic to be executed in a controlled manner.
    /// </summary>
    public interface IAsyncStartable
    {
        /// <summary>
        /// Asynchronously starts a process or service, executing any required initialization logic.
        /// </summary>
        /// <param name="cancellationToken">A token to monitor for cancellation requests to stop the start operation before completion.</param>
        /// <returns>A task that represents the asynchronous start operation.</returns>
        Task StartAsync(CancellationToken cancellationToken);
    }
}
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LMLocal.Core.Models.Instructions;

namespace LMLocal.Application.Abstractions.Ports
{
    /// <summary>
    /// Supplies the canonical set of built-in instruction tabs that ship with the extension.
    /// </summary>
    public interface IInstructionDefaultsProvider
    {
        /// <summary>
        /// Returns the built-in instruction tabs in their canonical (definition) order.
        /// </summary>
        Task<IReadOnlyList<InstructionDefaultTab>> GetDefaultTabsAsync(CancellationToken cancellationToken = default);
    }
}

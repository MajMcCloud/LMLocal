using System.Threading;
using System.Threading.Tasks;
using LMLocal.Core.Models.Instructions;

namespace LMLocal.Application.Abstractions.Ports
{
    /// <summary>
    /// Simple manager for instructions stored in a local JSON file.
    /// </summary>
    public interface IInstructionsManager
    {
        Task<string> GetAsync(CancellationToken cancellationToken = default);
        Task<InstructionUpdateResult> UpdateAsync(string jsonInstructions, CancellationToken cancellationToken = default);
        Task UpdateSelectedTabAsync(string selectedTabId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Finds the tab id (as string) of the first enabled instruction tab whose displayName matches the specified name (case-insensitive). Returns null if not found or disabled.
        /// </summary>
        Task<string> GetInstructionTabIdByDisplayNameAsync(string displayName, CancellationToken cancellationToken = default);
    }
}

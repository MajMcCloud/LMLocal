using System;
using System.Threading.Tasks;
using LMLocal.Application.Abstractions.Ports;
using LMLocal.Core.Common;
using LMLocal.Core.Models.Instructions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LMLocal.Infrastructure.WebView.Controllers
{
    /// <summary>
    /// Bridge class for communication between WebView2 and backend instructions logic.
    /// </summary>
    public interface IInstructionsController
    {
        Task<string> GetInstructionsAsync();

        /// <summary>
        /// Persists the full instructions document. Returns a JSON string of the shape <c>{ success: bool, error?: string }</c> so the UI can surface validation failures.
        /// </summary>
        Task<string> UpdateInstructionsAsync(string newInstructionsJson);

        Task<bool> UpdateInstructionsSelectedTabAsync(string selectedTabId);
    }

    [System.Runtime.InteropServices.ComVisible(true)]
    public class InstructionsController : IInstructionsController
    {
        private readonly IInstructionsManager _instructionsManager;

        public InstructionsController(IInstructionsManager instructionsManager)
        {
            _instructionsManager = instructionsManager ?? throw new ArgumentNullException(nameof(instructionsManager));
        }

        public async Task<string> GetInstructionsAsync()
        {
            try
            {
                return await _instructionsManager.GetAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                InternalLogger.Error("GetInstructionsAsync failed", ex);
                return "{}";
            }
        }

        public async Task<string> UpdateInstructionsAsync(string newInstructionsJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newInstructionsJson))
                    return BuildResult(false, "The instructions payload is empty.");

                InstructionUpdateResult result = await _instructionsManager.UpdateAsync(newInstructionsJson).ConfigureAwait(false);
                return BuildResult(result.Success, result.Error);
            }
            catch (Exception ex)
            {
                InternalLogger.Error("UpdateInstructionsAsync failed", ex);
                return BuildResult(false, ex.Message);
            }
        }

        public async Task<bool> UpdateInstructionsSelectedTabAsync(string selectedTabId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(selectedTabId))
                    return false;

                await _instructionsManager.UpdateSelectedTabAsync(selectedTabId).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("UpdateInstructionsSelectedTabAsync failed", ex);
                return false;
            }
        }

        private static string BuildResult(bool success, string error)
        {
            var payload = new JObject { ["success"] = success };
            if (!success)
                payload["error"] = string.IsNullOrWhiteSpace(error) ? "Unknown error." : error;

            return payload.ToString(Formatting.None);
        }
    }
}

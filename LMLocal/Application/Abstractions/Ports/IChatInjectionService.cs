using System.Threading.Tasks;

namespace LMLocal.Application.Abstractions.Ports
{
    /// <summary>
    /// Port for injecting content into the LM Local chat UI.
    /// </summary>
    public interface IChatInjectionService
    {
        /// <summary>
        /// Finds (or creates) the chat window, shows it, and injects the markdown text into the input field without sending it.
        /// </summary>
        Task InjectPromptAsync(string markdownText);

        /// <summary>
        /// Finds (or creates) the chat window, shows it, injects the markdown text and automatically sends it.
        /// </summary>
        Task InjectAndAutoSendAsync(string markdownText, string instructionTabId = null);
    }
}

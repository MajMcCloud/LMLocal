namespace LMLocal.Application.Abstractions.Ports
{
    /// <summary>
    /// Creates chat persistence services bound to an explicit directory.
    /// </summary>
    public interface IChatPersistenceFactory
    {
        /// <summary>
        /// Creates a persistence service bound to the given explicit directory (when null/empty, the configured local chat history directory is used).
        /// </summary>
        IChatPersistenceService Create(string explicitDirectory);
    }
}

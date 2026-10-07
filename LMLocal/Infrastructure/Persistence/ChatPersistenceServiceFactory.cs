using LMLocal.Application.Abstractions.Ports;

namespace LMLocal.Infrastructure.Persistence
{
    /// <summary>
    /// Default <see cref="IChatPersistenceFactory"/> implementation.
    /// </summary>
    internal class ChatPersistenceServiceFactory : IChatPersistenceFactory
    {
        private readonly ISettingsManager _settingsManager;
        private readonly IFileSystem _fileSystem;

        public ChatPersistenceServiceFactory(ISettingsManager settingsManager, IFileSystem fileSystem)
        {
            _settingsManager = settingsManager;
            _fileSystem = fileSystem;
        }

        public IChatPersistenceService Create(string explicitDirectory)
        {
            return new ChatPersistenceService(_settingsManager, _fileSystem, explicitDirectory);
        }
    }
}

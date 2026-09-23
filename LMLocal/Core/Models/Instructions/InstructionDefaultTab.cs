namespace LMLocal.Core.Models.Instructions
{
    /// <summary>
    /// Immutable description of a single built-in instruction tab.
    /// </summary>
    public sealed class InstructionDefaultTab
    {
        public InstructionDefaultTab(int id, string displayName, bool enabled, double temperature, string prompt)
        {
            Id = id;
            DisplayName = displayName;
            Enabled = enabled;
            Temperature = temperature;
            Prompt = prompt;
        }

        public int Id { get; }
        public string DisplayName { get; }
        public bool Enabled { get; }
        public double Temperature { get; }
        public string Prompt { get; }
    }
}

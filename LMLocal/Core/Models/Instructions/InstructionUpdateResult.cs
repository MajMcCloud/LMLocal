namespace LMLocal.Core.Models.Instructions
{
    /// <summary>
    /// Outcome of an instructions update attempt. Carries a user-friendly error message when <see cref="Success"/> is false.
    /// </summary>
    public sealed class InstructionUpdateResult
    {
        private InstructionUpdateResult(bool success, string error)
        {
            Success = success;
            Error = error;
        }

        public bool Success { get; }

        public string Error { get; }

        public static InstructionUpdateResult Ok() => new InstructionUpdateResult(true, null);

        public static InstructionUpdateResult Fail(string error) => new InstructionUpdateResult(false, error);
    }
}

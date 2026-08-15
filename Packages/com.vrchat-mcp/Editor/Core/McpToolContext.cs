using VrcMcp.Batch;

namespace VrcMcp.Core
{
    /// <summary>Execution context passed to every tool invocation.</summary>
    public class McpToolContext
    {
        public string ToolName { get; internal set; }
        public BatchStateMachine Batch => BatchStateMachine.Instance;
    }
}
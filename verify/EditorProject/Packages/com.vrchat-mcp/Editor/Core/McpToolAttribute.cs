using System;

namespace VrcMcp.Core
{
    /// <summary>
    /// Marks a static method as an MCP tool. Signature:
    /// <code>static string MethodName(string argsJson, McpToolContext ctx)</code>
    /// The return value is the JSON result string for the MCP tool result.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class McpToolAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }

        public McpToolAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }
}
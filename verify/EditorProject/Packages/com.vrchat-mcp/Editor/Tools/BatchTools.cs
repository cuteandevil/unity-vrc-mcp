using System.Text;
using UnityEditor;
using VrcMcp.Batch;
using VrcMcp.Core;
using Newtonsoft.Json.Linq;

namespace VrcMcp.Tools
{
    public static class BatchTools
    {
        [McpTool("begin_batch",
            "Explicit Undo transaction: all following tool calls collapse into ONE undo step. " +
            "Params: {\"name\":\"optional batch name\"}. Default: one tool call = one undo group.")]
        public static string BeginBatch(string argsJson, McpToolContext ctx)
        {
            string name = null;
            try
            {
                int idx = argsJson.IndexOf("\"name\"", System.StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int colon = argsJson.IndexOf(':', idx + 6);
                    int start = argsJson.IndexOf('"', colon + 1);
                    if (start >= 0)
                    {
                        int end = argsJson.IndexOf('"', start + 1);
                        if (end >= 0) name = argsJson.Substring(start + 1, end - start - 1);
                    }
                }
            }
            catch { }
            ctx.Batch.Begin(name);
            ctx.Batch.ToolCompleted();
            return BatchStateJson(ctx);
        }

        [McpTool("end_batch", "Closes the explicit transaction and collapses it into a single undo step.")]
        public static string EndBatch(string argsJson, McpToolContext ctx)
        {
            ctx.Batch.End();
            return BatchStateJson(ctx);
        }

        [McpTool("get_batch_state", "Current batch state machine details (phase, idle seconds, close reason).")]
        public static string GetBatchState(string argsJson, McpToolContext ctx)
        {
            return BatchStateJson(ctx);
        }

        [McpTool("undo", "Performs a single editor undo (Undo.PerformUndo).")]
        public static string UndoAction(string argsJson, McpToolContext ctx)
        {
            // Programmatic edits never hit Unity's "mouse-up" flush points, so
            // RecordObject'd changes sit unregistered in the undo stack. Flush
            // them first or PerformUndo has nothing to undo (DESIGN §0 lesson).
            UnityEditor.Undo.FlushUndoRecordObjects();
            UnityEditor.Undo.PerformUndo();
            return "{\"performed\":true}";
        }

        [McpTool("redo", "Performs a single editor redo (Undo.PerformRedo).")]
        public static string RedoAction(string argsJson, McpToolContext ctx)
        {
            UnityEditor.Undo.FlushUndoRecordObjects();
            UnityEditor.Undo.PerformRedo();
            return "{\"performed\":true}";
        }

        private static JObject BatchState(McpToolContext ctx)
        {
            var b = ctx.Batch;
            bool closed = b.Phase == BatchPhase.Closed;
            var r = new JObject();
            r["phase"] = b.Phase.ToString();
            r["name"] = b.Name ?? "";
            r["groupIndex"] = b.GroupIndex;
            r["openedAt"] = b.OpenedAt.ToString("O");
            r["idleSeconds"] = closed ? 0.0 : b.IdleSeconds;
            r["idleTimeoutSeconds"] = b.IdleTimeoutSeconds;
            r["closeReason"] = b.CloseReason ?? "";
            return r;
        }

        internal static string BatchStateJson(McpToolContext ctx)
        {
            return BatchState(ctx).ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
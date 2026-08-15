using System;
using UnityEditor;
using UnityEngine;

namespace VrcMcp.Batch
{
    public enum BatchCloseReason { Manual, Disconnect, IdleTimeout }

    public enum BatchPhase { Closed, Active, AwaitingApproval }

    /// <summary>
    /// Undo transaction state machine (three-dimensional: disconnect / idle / approval).
    /// Default semantics: one tool call = one Undo group. begin_batch/end_batch create
    /// an explicit multi-tool transaction collapsed into a single undo step.
    /// </summary>
    public class BatchStateMachine
    {
        private const double DefaultIdleTimeoutSeconds = 600.0;

        private double _idleAccumulator;

        public static BatchStateMachine Instance { get; } = new BatchStateMachine();

        public BatchPhase Phase { get; private set; } = BatchPhase.Closed;
        public string Name { get; private set; }
        public int GroupIndex { get; private set; }
        public DateTimeOffset OpenedAt { get; private set; }
        public DateTimeOffset LastActivityAt { get; private set; }
        public double IdleTimeoutSeconds { get; set; } = DefaultIdleTimeoutSeconds;
        public string CloseReason { get; private set; }

        public event Action<BatchCloseReason, string> BatchClosed;

        private BatchStateMachine() { }

        public void Begin(string name)
        {
            if (Phase != BatchPhase.Closed) ForceClose(BatchCloseReason.Manual, "superseded by new batch");
            Phase = BatchPhase.Active;
            Name = string.IsNullOrEmpty(name) ? "vrc-mcp-batch" : name;
            OpenedAt = DateTimeOffset.Now;
            LastActivityAt = OpenedAt;
            _idleAccumulator = 0;
            GroupIndex = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Name);
            Debug.Log($"[VrcMcp] batch begin '{Name}' group={GroupIndex}");
        }

        /// <summary>Collapses all undo operations since the batch started into one step.</summary>
        public void End()
        {
            if (Phase == BatchPhase.Closed) return;
            ForceClose(BatchCloseReason.Manual, "end_batch");
            try
            {
                // Programmatic edits need an explicit flush before the undo stack
                // can be collapsed (same lesson as BatchTools.UndoAction).
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(GroupIndex);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VrcMcp] collapse failed: " + e.Message);
            }
        }

        /// <summary>Called by the bridge when all clients disconnected: immediate close, no timer.</summary>
        public void EndOnDisconnect()
        {
            if (Phase != BatchPhase.Closed) ForceClose(BatchCloseReason.Disconnect, "client disconnected");
        }

        public void ToolCompleted()
        {
            if (Phase == BatchPhase.Closed) return;
            LastActivityAt = DateTimeOffset.Now;
            _idleAccumulator = 0;
        }

        /// <summary>Non-blocking approval: idle timer pauses while pending.</summary>
        public void SetApprovalPending(bool pending)
        {
            if (Phase == BatchPhase.Closed) return;
            Phase = pending ? BatchPhase.AwaitingApproval : BatchPhase.Active;
            LastActivityAt = DateTimeOffset.Now;
            _idleAccumulator = 0;
        }

        public double IdleSeconds => Math.Max(0, (DateTimeOffset.Now - LastActivityAt).TotalSeconds);

        public void Tick(double deltaSeconds)
        {
            if (Phase != BatchPhase.Active) return;
            _idleAccumulator += deltaSeconds;
            if (_idleAccumulator >= IdleTimeoutSeconds)
                ForceClose(BatchCloseReason.IdleTimeout, "idle_timeout");
        }

        private void ForceClose(BatchCloseReason reason, string detail)
        {
            var prev = Phase;
            Phase = BatchPhase.Closed;
            CloseReason = reason.ToString();
            BatchClosed?.Invoke(reason, detail);
            if (prev != BatchPhase.Closed)
                Debug.Log($"[VrcMcp] batch closed ({reason}: {detail})");
        }
    }

    /// <summary>Self-ticking idle timer on the editor update loop.</summary>
    public static class BatchTicker
    {
        private static double _lastTick = -1;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            EditorApplication.update += OnUpdate;
        }

        private static void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_lastTick < 0) { _lastTick = now; return; }
            double dt = now - _lastTick;
            _lastTick = now;
            BatchStateMachine.Instance.Tick(dt);
        }
    }
}
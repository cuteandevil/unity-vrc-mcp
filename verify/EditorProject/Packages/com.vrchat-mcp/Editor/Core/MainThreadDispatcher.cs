using System;
using System.Collections.Concurrent;
using UnityEditor;
using UnityEngine;

namespace VrcMcp.Core
{
    /// <summary>
    /// Marshals work to the Unity main thread. All Unity API access must happen
    /// through this queue (or be triggered directly on the main thread).
    /// </summary>
    public static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        private static bool _initialized;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            EditorApplication.update += Pump;
        }

        public static void Enqueue(Action action)
        {
            if (action == null) return;
            if (EditorApplication.isCompiling || !_initialized)
            {
                EditorApplication.delayCall += () => action();
                return;
            }
            Queue.Enqueue(action);
        }

        public static void Enqueue<T>(Action<T> action, T arg)
        {
            Enqueue(() => action(arg));
        }

        private static void Pump()
        {
            int budget = 256; // guard against starving the editor loop
            while (budget-- > 0 && Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
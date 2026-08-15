using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VrcMcp.Core;
using VrcMcp.Handshake;
using Newtonsoft.Json.Linq;

namespace VrcMcp.Tools
{
    /// <summary>
    /// Phase 3 batch 2: AnimatorController / AnimatorStateMachine tools.
    ///
    /// DESIGN CONSTRAINT (spike §22): the asset file is NOT controlled by Undo -
    /// PerformUndo only rolls back in-memory objects. Therefore:
    ///   - these tools must be used OUTSIDE a batch (like import tools);
    ///   - RegisterCreatedObjectUndo is FORBIDDEN for asset creation (it leaves the
    ///     memory/disk double-state after undo);
    ///   - rollback is tool-level transaction: on failure, the tool deletes the
    ///     assets it created in this call (recorded before any mutation).
    /// </summary>
    public static class AnimatorTools
    {
        [McpTool("create_animator_controller",
            "Create a new AnimatorController asset (AnimatorController.CreateAnimatorControllerAtPath) " +
            "with one layer containing a single 'Idle' state, and optionally bind it to a scene " +
            "GameObject's Animator component (adds Animator if missing). " +
            "Params: {\"assetPath\":\"Assets/MyAvatar/AC.controller\",\"bindInstanceId\":123}. " +
            "assetPath must end in .controller and live under Assets/. " +
            "ASSET OPERATION - not undoable, do NOT call inside a batch. On failure the tool " +
            "deletes assets it created in this call.")]
        public static string CreateAnimatorController(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<AnimatorCreateArgs>(argsJson ?? "{}");
            if (string.IsNullOrEmpty(a.assetPath) || !a.assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                || !a.assetPath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException("assetPath must be a non-empty Assets/*.controller path");

            // AssetDatabase is authoritative for asset existence. File.Exists on the
            // raw path can be fooled by stale SourceAssetDB records (file gone from
            // disk but DB records remain, e.g. after a DeleteAsset crash), and
            // LoadAssetAtPath can return a live instance of a DELETED controller still
            // referenced by a bound Animator.
            // Rule: DB says exists + file on disk -> real conflict, refuse.
            //       DB says exists + file gone -> orphan reference, force-clean then create.
            //       DB says gone -> create.
            // NOTE: earlier versions speculated Unity 6 VFS "materializes" ghost files
            // during Refresh/typed loads; extensive e2e testing disproved this - a file
            // on disk after create is simply the created file (see DESIGN.md §24).
            // Order still matters: check File.Exists BEFORE any typed load so a stale
            // DB record can never make File.Exists report a file that is not there.
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var absPath = System.IO.Path.Combine(ChannelFile.ProjectDir, a.assetPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(absPath))
                throw new McpToolException($"asset already exists: {a.assetPath} (abs={absPath})");
            // Disk is clean; purge any stale DB record (a typed load may return a live
            // orphan instance, which DeleteAsset then removes), then create fresh.
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(a.assetPath) != null)
                AssetDatabase.DeleteAsset(a.assetPath);

            var created = new List<string> { a.assetPath };
            try
            {
                var ac = AnimatorController.CreateAnimatorControllerAtPath(a.assetPath);
                if (ac == null)
                    throw new McpToolException($"CreateAnimatorControllerAtPath returned null for {a.assetPath} (abs={absPath})");
                var layer = ac.layers.Length > 0 ? ac.layers[0] : null;
                if (layer != null && layer.stateMachine != null && layer.stateMachine.states.Length == 0)
                {
                    var state = layer.stateMachine.AddState("Idle");
                    state.writeDefaultValues = true;
                }
                AssetDatabase.SaveAssets();

                var r = new JObject();
                r["assetPath"] = a.assetPath;
                r["layers"] = ac.layers.Length;
                r["states"] = layer != null && layer.stateMachine != null
                    ? layer.stateMachine.states.Length : 0;
                if (a.bindInstanceId != 0)
                {
                    var go = EditValidation.ResolveGameObject(a.bindInstanceId);
                    var animator = go.GetComponent<Animator>();
                    if (animator == null)
                        animator = go.AddComponent<Animator>();
                    animator.runtimeAnimatorController = ac;
                    r["boundInstanceId"] = go.GetInstanceID();
                }
                return r.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                foreach (var path in created)
                {
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null)
                        AssetDatabase.DeleteAsset(path);
                }
                throw;
            }
        }

        [McpTool("add_animator_state",
            "Add a state to a controller's layer (default: layer 0, the base layer) and " +
            "optionally set its motion to a clip (created via AnimationClip.SetCurve, one " +
            "dummy curve on the given binding path). " +
            "Params: {\"assetPath\":\"Assets/MyAvatar/AC.controller\",\"stateName\":\"Run\",\"layer\":0,\"withClip\":true,\"clipPath\":\"Assets/MyAvatar/Run.anim\"}. " +
            "Returns the state name and the number of states in the layer after the add. " +
            "ASSET OPERATION - not undoable, do NOT call inside a batch.")]
        public static string AddAnimatorState(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<AnimatorStateArgs>(argsJson ?? "{}");
            var ac = LoadController(a.assetPath);
            if (a.layer < 0 || a.layer >= ac.layers.Length)
                throw new McpToolException($"layer {a.layer} out of range (0..{ac.layers.Length - 1})");
            var sm = ac.layers[a.layer].stateMachine;
            if (string.IsNullOrEmpty(a.stateName))
                throw new McpToolException("stateName required");

            var created = new List<string>();
            string clipPath = null;
            try
            {
                AnimationClip clip = null;
                if (a.withClip)
                {
                    clipPath = string.IsNullOrEmpty(a.clipPath)
                        ? System.IO.Path.ChangeExtension(a.assetPath, null) + "_" + a.stateName + ".anim"
                        : a.clipPath;
                    if (!clipPath.StartsWith("Assets/", StringComparison.Ordinal)
                        || !clipPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                        throw new McpToolException("clipPath must be Assets/*.anim");
                    if (AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath) != null)
                        throw new McpToolException($"clip asset already exists: {clipPath}");
                    clip = new AnimationClip();
                    var curve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 0f));
                    clip.SetCurve(a.stateName, typeof(Transform), "localPosition.x", curve);
                    AssetDatabase.CreateAsset(clip, clipPath);
                    created.Add(clipPath);
                }

                var state = sm.AddState(a.stateName);
                state.writeDefaultValues = true;
                if (clip != null)
                    state.motion = clip;
                AssetDatabase.SaveAssets();

                var r = new JObject();
                r["stateName"] = state.name;
                r["statesInLayer"] = sm.states.Length;
                if (clip != null)
                    r["clipPath"] = clipPath;
                return r.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                foreach (var path in created)
                {
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null)
                        AssetDatabase.DeleteAsset(path);
                }
                throw;
            }
        }

        [McpTool("add_animator_transition",
            "Add a transition between two states in a layer (default layer 0). " +
            "Params: {\"assetPath\":\"Assets/MyAvatar/AC.controller\",\"from\":\"Idle\",\"to\":\"Run\",\"layer\":0,\"duration\":0.25}. " +
            "Creates AnyState? No - from/to must be existing state names. Returns transition count of 'from'. " +
            "ASSET OPERATION - not undoable, do NOT call inside a batch.")]
        public static string AddAnimatorTransition(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<AnimatorTransitionArgs>(argsJson ?? "{}");
            var ac = LoadController(a.assetPath);
            if (a.layer < 0 || a.layer >= ac.layers.Length)
                throw new McpToolException($"layer {a.layer} out of range (0..{ac.layers.Length - 1})");
            var sm = ac.layers[a.layer].stateMachine;
            var from = FindState(sm, a.from);
            var to = FindState(sm, a.to);
            if (from == null) throw new McpToolException($"state not found: {a.from}");
            if (to == null) throw new McpToolException($"state not found: {a.to}");

            var t = from.AddTransition(to);
            t.duration = a.duration > 0 ? a.duration : 0f;
            t.hasExitTime = true;
            t.exitTime = 0f;
            AssetDatabase.SaveAssets();

            var r = new JObject();
            r["from"] = from.name;
            r["to"] = to.name;
            r["transitions"] = from.transitions.Length;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("get_animator_controller",
            "Read-only: dump an AnimatorController asset - layers, states, transitions. " +
            "Params: {\"assetPath\":\"Assets/MyAvatar/AC.controller\"}. Returns structured JSON.")]
        public static string GetAnimatorController(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<AssetPathArgs>(argsJson ?? "{}");
            var ac = LoadController(a.assetPath);
            var root = new JObject();
            root["assetPath"] = a.assetPath;
            var layers = new JArray();
            for (int i = 0; i < ac.layers.Length; i++)
            {
                var layer = ac.layers[i];
                var jl = new JObject();
                jl["name"] = layer.name;
                var states = layer.stateMachine.states;
                var jstates = new JArray();
                for (int s = 0; s < states.Length; s++)
                {
                    var js = new JObject();
                    js["name"] = states[s].state.name;
                    js["motion"] = states[s].state.motion != null ? "clip" : null;
                    var trans = states[s].state.transitions;
                    var jtrans = new JArray();
                    for (int t = 0; t < trans.Length; t++)
                    {
                        var jt = new JObject();
                        jt["to"] = trans[t].destinationState != null ? trans[t].destinationState.name : "";
                        jt["duration"] = trans[t].duration;
                        jtrans.Add(jt);
                    }
                    js["transitions"] = jtrans;
                    jstates.Add(js);
                }
                jl["states"] = jstates;
                layers.Add(jl);
            }
            root["layers"] = layers;
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ---------- helpers ----------

        private static AnimatorController LoadController(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                || !assetPath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException("assetPath must be a non-empty Assets/*.controller path");
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (ac == null)
                throw new McpToolException($"no AnimatorController at {assetPath}");
            return ac;
        }

        private static AnimatorState FindState(AnimatorStateMachine sm, string name)
        {
            foreach (var s in sm.states)
                if (s.state.name == name) return s.state;
            return null;
        }

        [Serializable]
        private class AnimatorCreateArgs { public string assetPath; public int bindInstanceId; }

        [Serializable]
        private class AnimatorStateArgs { public string assetPath; public string stateName; public int layer; public bool withClip; public string clipPath; }

        [Serializable]
        private class AnimatorTransitionArgs { public string assetPath; public string from; public string to; public int layer; public float duration; }

        [Serializable]
        private class AssetPathArgs { public string assetPath; }
    }
}
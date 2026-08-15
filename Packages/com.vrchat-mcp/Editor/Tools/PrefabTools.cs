using System;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using VrcMcp.Core;
using VrcMcp.Handshake;

namespace VrcMcp.Tools
{
    /// <summary>
    /// Phase 2 batch 2 prefab tools. prefab_create saves the current state of a scene
    /// object as a Prefab asset (PrefabUtility.SaveAsPrefabAsset); prefab_instantiate
    /// spawns an instance (PrefabUtility.InstantiatePrefab + RegisterCreatedObjectUndo).
    /// All writes are undoable and go through the edit validation layer.
    /// </summary>
    public static class PrefabTools
    {
        [McpTool("prefab_create",
            "Save a scene object as a Prefab asset under the project Assets folder. " +
            "Params: {\"instanceId\":123,\"path\":\"Assets/MyPrefab.prefab\"}. " +
            "path must end in .prefab and live under Assets/. Returns the saved asset path.")]
        public static string PrefabCreate(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<PrefabCreateArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);
            ValidatePrefabPath(a.path);

            // PrefabUtility internally opens its own undo records; fold them back into
            // the current group so batch single-undo still covers the whole transaction.
            int group = Undo.GetCurrentGroup();
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(a.path);
            Undo.SetCurrentGroupName("MCP prefab_create");
            GameObject saved = null;
            if (existing != null)
            {
                saved = PrefabUtility.SaveAsPrefabAssetAndConnect(go, a.path, InteractionMode.UserAction);
            }
            else
            {
                saved = PrefabUtility.SaveAsPrefabAsset(go, a.path);
            }
            Undo.CollapseUndoOperations(group);
            if (saved == null)
                throw new McpToolException($"failed to save prefab at {a.path}");

            var r = new JObject();
            r["path"] = a.path;
            r["name"] = saved.name;
            r["instanceId"] = saved.GetInstanceID();
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("prefab_instantiate",
            "Instantiate a prefab asset into the scene. " +
            "Params: {\"path\":\"Assets/MyPrefab.prefab\",\"parentInstanceId\":123,\"position\":[x,y,z]}. " +
            "parent optional (defaults to scene root). Returns the new instanceId.")]
        public static string PrefabInstantiate(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<PrefabInstantiateArgs>(argsJson ?? "{}");
            ValidatePrefabPath(a.path);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(a.path);
            if (asset == null)
                throw new McpToolException($"no prefab at {a.path}");

            Transform parent = null;
            if (a.parentInstanceId != 0)
                parent = EditValidation.ResolveGameObject(a.parentInstanceId).transform;

            // Fold PrefabUtility's internal undo records back into the current group
            // (same reasoning as prefab_create: batch single-undo must cover this).
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("MCP prefab_instantiate");
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            if (inst == null)
                throw new McpToolException($"failed to instantiate prefab at {a.path}");
            if (parent != null) inst.transform.SetParent(parent, false);
            if (a.position != null)
                inst.transform.localPosition = EditValidation.Vec3(a.position, "position");

            Undo.RegisterCreatedObjectUndo(inst, "MCP prefab_instantiate");
            Undo.CollapseUndoOperations(group);
            EditorUtility.SetDirty(inst);

            var r = new JObject();
            r["instanceId"] = inst.GetInstanceID();
            r["name"] = inst.name;
            r["parentInstanceId"] = parent != null ? parent.gameObject.GetInstanceID() : 0;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("asset_delete",
            "Delete an asset from the project (AssetDatabase.DeleteAsset). " +
            "Params: {\"path\":\"Assets/MyPrefab.prefab\"}. Must live under Assets/. " +
            "Tests must use this instead of unlink() so Unity's asset DB stays consistent.")]
        public static string AssetDelete(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<AssetDeleteArgs>(argsJson ?? "{}");
            if (string.IsNullOrEmpty(a.path) || !a.path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new McpToolException("path must be a non-empty Assets/ path");
            var abs = System.IO.Path.Combine(ChannelFile.ProjectDir, a.path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            bool fileExists = System.IO.File.Exists(abs);
            bool metaExists = System.IO.File.Exists(abs + ".meta");
            // NOTE: LoadAssetAtPath<UnityEngine.Object> returns null for multi-sub-asset
            // types like .controller (it resolves the MAIN asset only via a typed query),
            // so dbKnows=false even when the file IS known+loaded. DeleteAsset then
            // refuses and the file survives forever (observed: AnimTestAC.controller
            // across restarts). Use LoadAllAssetsAtPath for the real answer.
            var all = AssetDatabase.LoadAllAssetsAtPath(a.path);
            bool dbKnows = all != null && all.Length > 0;
            // Detach any live scene references first: a bound Animator keeps the deleted
            // controller alive in memory and the scene file, and Unity then rewrites the
            // file back onto disk on the next save/traversal (observed: animator regression
            // create refused with "already exists" after a bound controller was deleted;
            // the file reappeared on disk). This is ordinary reference-keeping, NOT the
            // VFS "materialization" previously speculated here (see DESIGN.md §24).
            foreach (var anim in UnityEngine.Object.FindObjectsOfType<Animator>(true))
            {
                if (anim.runtimeAnimatorController != null
                    && AssetDatabase.GetAssetPath(anim.runtimeAnimatorController) == a.path)
                    anim.runtimeAnimatorController = null;
            }
            bool deleted = AssetDatabase.DeleteAsset(a.path);
            if (!deleted && fileExists)
            {
                // DB does not know this file (e.g. stale SourceAssetDB entry or a
                // multi-asset query miss). DeleteAsset refuses; remove on disk and
                // let the DB catch up via Refresh instead of leaving a ghost.
                try
                {
                    if (metaExists) System.IO.File.Delete(abs + ".meta");
                    System.IO.File.Delete(abs);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    deleted = true;
                }
                catch (Exception ex)
                {
                    throw new McpToolException(
                        $"failed to delete asset at {a.path} (fileExists={fileExists}, metaExists={metaExists}, " +
                        $"dbKnows={dbKnows}, diskDeleteError={ex.Message})");
                }
            }
            if (!deleted)
                throw new McpToolException(
                    $"failed to delete asset at {a.path} (fileExists={fileExists}, metaExists={metaExists}, dbKnows={dbKnows})");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var r = new JObject();
            r["deleted"] = a.path;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void ValidatePrefabPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new McpToolException("path must not be empty");
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException("path must end in .prefab");
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new McpToolException("path must live under Assets/");
        }

        [Serializable]
        private class PrefabCreateArgs { public int instanceId; public string path; }

        [Serializable]
        private class PrefabInstantiateArgs { public string path; public int parentInstanceId; public float[] position; }

        [Serializable]
        private class AssetDeleteArgs { public string path; }
    }
}
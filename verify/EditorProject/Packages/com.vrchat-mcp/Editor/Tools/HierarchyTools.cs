using System;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using VrcMcp.Core;

namespace VrcMcp.Tools
{
    /// <summary>
    /// Phase 2 batch 2 hierarchy tools. Every tool call = one undo group (unless inside
    /// a batch). Create/destroy/duplicate go through Undo.* so a single undo restores
    /// the full structure change.
    /// </summary>
    public static class HierarchyTools
    {
        [McpTool("edit_set_parent",
            "Reparent a game object. Params: {\"instanceId\":123,\"newParentInstanceId\":456} " +
            "or newParentInstanceId=0 to unparent (make it a scene root). " +
            "Optional {\"worldPositionStays\":true} (default true).")]
        public static string EditSetParent(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<SetParentArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);
            if (a.newParentInstanceId == a.instanceId)
                throw new McpToolException("cannot reparent an object to itself");

            Transform newParent = null;
            if (a.newParentInstanceId != 0)
            {
                var parentGo = EditValidation.ResolveGameObject(a.newParentInstanceId);
                newParent = parentGo.transform;
                // a cycle is when the new parent is the object itself or one of its
                // descendants (IsChildOf is true for ancestors too, which is legal)
                if (newParent.IsChildOf(go.transform))
                    throw new McpToolException("cannot reparent to itself or a descendant (cycle)");
            }

            var oldParent = go.transform.parent;
            Undo.SetCurrentGroupName("MCP edit_set_parent");
            Undo.RecordObject(go.transform, "MCP edit_set_parent");
            Undo.RecordObject(go, "MCP edit_set_parent");
            go.transform.SetParent(newParent, a.worldPositionStays);
            if (oldParent != null) Undo.RecordObject(oldParent, "MCP edit_set_parent");
            EditorUtility.SetDirty(go);

            var r = new JObject();
            r["instanceId"] = go.GetInstanceID();
            r["name"] = go.name;
            r["parentInstanceId"] = newParent != null ? newParent.gameObject.GetInstanceID() : 0;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("edit_set_sibling_index",
            "Reorder an object among its siblings. Params: {\"instanceId\":123,\"index\":0} " +
            "(-1 or omitted = move to end).")]
        public static string EditSetSiblingIndex(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<SiblingArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);
            var t = go.transform;
            if (t.parent == null)
                throw new McpToolException("object has no parent; sibling index only applies to children");

            int target = a.index < 0 ? t.parent.childCount - 1 : a.index;
            if (target >= t.parent.childCount)
                throw new McpToolException($"index {a.index} out of range (parent has {t.parent.childCount} children)");

            Undo.SetCurrentGroupName("MCP edit_set_sibling_index");
            Undo.RecordObject(t.parent, "MCP edit_set_sibling_index");
            t.SetSiblingIndex(target);
            EditorUtility.SetDirty(go);
            var r = new JObject();
            r["instanceId"] = go.GetInstanceID();
            r["siblingIndex"] = t.GetSiblingIndex();
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("edit_create_object",
            "Create an empty game object. Params: {\"name\":\"NewObject\",\"parentInstanceId\":123} " +
            "(parent optional; omit for scene root). Optional {\"position\":[x,y,z]}. Returns instanceId.")]
        public static string EditCreateObject(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<CreateArgs>(argsJson ?? "{}");
            if (string.IsNullOrEmpty(a.name))
                throw new McpToolException("name must not be empty");

            Transform parent = null;
            if (a.parentInstanceId != 0)
                parent = EditValidation.ResolveGameObject(a.parentInstanceId).transform;

            var go = new GameObject(a.name);
            if (parent != null) go.transform.SetParent(parent, false);
            if (a.position != null)
                go.transform.localPosition = EditValidation.Vec3(a.position, "position");

            Undo.SetCurrentGroupName("MCP edit_create_object");
            Undo.RegisterCreatedObjectUndo(go, "MCP edit_create_object");
            EditorUtility.SetDirty(go);

            var r = new JObject();
            r["instanceId"] = go.GetInstanceID();
            r["name"] = go.name;
            r["parentInstanceId"] = parent != null ? parent.gameObject.GetInstanceID() : 0;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("edit_destroy_object",
            "Destroy a game object (undoable). Params: {\"instanceId\":123}.")]
        public static string EditDestroyObject(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<DestroyArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);

            Undo.SetCurrentGroupName("MCP edit_destroy_object");
            var name = go.name;
            Undo.DestroyObjectImmediate(go);
            var r = new JObject();
            r["destroyed"] = name;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("edit_duplicate_object",
            "Duplicate a game object (undoable; exact clone incl. children, no transform change). " +
            "Params: {\"instanceId\":123}.")]
        public static string EditDuplicateObject(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<DuplicateArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("MCP edit_duplicate_object");
            var clone = (GameObject)PrefabUtility.InstantiatePrefab(go);
            if (clone == null)
            {
                // not a prefab instance (or prefab asset can't be instantiated as-is):
                // fall back to a plain instantiate
                clone = UnityEngine.Object.Instantiate(go, go.transform.parent);
                clone.name = go.name;
            }
            Undo.RegisterCreatedObjectUndo(clone, "MCP edit_duplicate_object");
            Undo.CollapseUndoOperations(group);

            EditorUtility.SetDirty(clone);
            var r = new JObject();
            r["instanceId"] = clone.GetInstanceID();
            r["name"] = clone.name;
            r["parentInstanceId"] = clone.transform.parent != null ? clone.transform.parent.gameObject.GetInstanceID() : 0;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [Serializable]
        private class SetParentArgs { public int instanceId; public int newParentInstanceId; public bool worldPositionStays = true; }

        [Serializable]
        private class SiblingArgs { public int instanceId; public int index = -1; }

        [Serializable]
        private class CreateArgs { public string name; public int parentInstanceId; public float[] position; }

        [Serializable]
        private class DestroyArgs { public int instanceId; }

        [Serializable]
        private class DuplicateArgs { public int instanceId; }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VrcMcp.Core;
using Newtonsoft.Json.Linq;

namespace VrcMcp.Tools
{
    /// <summary>User-facing validation error (clean message, no stack in the error envelope).</summary>
    public sealed class McpToolException : Exception
    {
        public McpToolException(string message) : base(message) { }
    }

    /// <summary>Phase 2 edit tools. Every tool call = one undo group (unless inside a batch).</summary>
    public static class EditTools
    {
        // ---------- read side (edit prerequisite) ----------

        [McpTool("get_object_details",
            "Full detail for one object: transforms, components, serialized properties. " +
            "Params: {\"instanceId\":123,\"maxProperties\":64}")]
        public static string GetObjectDetails(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<DetailsArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);
            if (a.maxProperties <= 0) a.maxProperties = 64;

            var d = new JObject();
            d["instanceId"] = go.GetInstanceID();
            d["name"] = go.name;
            d["activeSelf"] = go.activeSelf;
            d["activeInHierarchy"] = go.activeInHierarchy;
            d["layer"] = go.layer;
            d["tag"] = go.tag;
            var scene = go.scene;
            d["sceneName"] = scene.isLoaded ? scene.name : "";
            d["scenePath"] = scene.isLoaded ? scene.path : "";
            d["hierarchyPath"] = HierarchyPath(go.transform);
            var t = go.transform;
            d["localPosition"] = new JArray(t.localPosition.x, t.localPosition.y, t.localPosition.z);
            var le = t.localEulerAngles;
            d["localRotation"] = new JArray(le.x, le.y, le.z);
            d["localScale"] = new JArray(t.localScale.x, t.localScale.y, t.localScale.z);
            var wp = t.position;
            d["position"] = new JArray(wp.x, wp.y, wp.z);
            var we = t.eulerAngles;
            d["rotation"] = new JArray(we.x, we.y, we.z);
            var ws = t.lossyScale;
            d["lossyScale"] = new JArray(ws.x, ws.y, ws.z);
            d["parentInstanceId"] = t.parent != null ? t.parent.gameObject.GetInstanceID() : 0;
            d["siblingIndex"] = t.GetSiblingIndex();
            d["childCount"] = t.childCount;
            var components = go.GetComponents<Component>();
            var comps = new JArray();
            foreach (var c in components)
            {
                var jc = new JObject();
                jc["type"] = c != null ? c.GetType().Name : "missing";
                jc["instanceId"] = c != null ? c.GetInstanceID() : 0;
                var b = c as Behaviour;
                jc["enabled"] = b != null ? (JToken)(b.enabled ? true : false) : null;
                if (c != null)
                    jc["properties"] = WriteSerializedProperties(c, a.maxProperties);
                comps.Add(jc);
            }
            d["components"] = comps;
            return d.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string HierarchyPath(Transform t)
        {
            var parts = new List<string>();
            var cur = t;
            while (cur != null)
            {
                parts.Add(cur.gameObject.name);
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static JObject WriteSerializedProperties(Component c, int max)
        {
            var so = new SerializedObject(c);
            var it = so.GetIterator();
            var root = new JObject();
            int count = 0;
            // NextVisible() returns nothing for some built-in components (e.g. AudioListener on
            // 6000.4) because of visibility rules; Next(true) always works, we filter internals.
            while (it.Next(true))
            {
                if (IsInternalPath(it.propertyPath)) continue;
                if (count >= max)
                {
                    root["...truncated"] = true;
                    count++;
                    break;
                }
                root[it.propertyPath] = WritePropertyValue(it);
                count++;
            }
            return root;
        }

        private static bool IsInternalPath(string p)
        {
            if (p == "m_ObjectHideFlags" || p == "m_Script" || p == "m_EditorClassIdentifier") return true;
            if (p == "m_GameObject" || p == "m_CorrespondingSourceObject"
                || p == "m_PrefabInstance" || p == "m_PrefabAsset") return true;
            return p.StartsWith("m_CorrespondingSourceObject.")
                || p.StartsWith("m_PrefabInstance.")
                || p.StartsWith("m_PrefabAsset.")
                || p.StartsWith("m_GameObject.");
        }

        private static JToken WritePropertyValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Float:
                    return new JValue(p.floatValue);
                case SerializedPropertyType.Integer:
                    return new JValue(p.longValue);
                case SerializedPropertyType.Boolean:
                    return new JValue(p.boolValue);
                case SerializedPropertyType.String:
                    return new JValue(p.stringValue);
                case SerializedPropertyType.Enum:
                    var je = new JObject();
                    je["name"] = p.enumNames.Length > p.enumValueIndex ? p.enumNames[p.enumValueIndex] : "?";
                    je["value"] = p.intValue;
                    return je;
                case SerializedPropertyType.Vector2:
                    return new JArray(p.vector2Value.x, p.vector2Value.y);
                case SerializedPropertyType.Vector3:
                    return new JArray(p.vector3Value.x, p.vector3Value.y, p.vector3Value.z);
                case SerializedPropertyType.Vector4:
                    return new JArray(p.vector4Value.x, p.vector4Value.y, p.vector4Value.z, p.vector4Value.w);
                case SerializedPropertyType.Quaternion:
                    return new JArray(p.quaternionValue.x, p.quaternionValue.y, p.quaternionValue.z, p.quaternionValue.w);
                case SerializedPropertyType.Color:
                    return new JArray(p.colorValue.r, p.colorValue.g, p.colorValue.b, p.colorValue.a);
                case SerializedPropertyType.ObjectReference:
                    var o = p.objectReferenceValue;
                    if (o == null) return null;
                    var jo = new JObject();
                    jo["name"] = o.name;
                    jo["instanceId"] = o.GetInstanceID();
                    return jo;
                case SerializedPropertyType.ArraySize:
                    return new JValue(p.intValue);
                default:
                    // struct / array / etc: report shape only, keep volume bounded
                    var j = new JObject();
                    j["__type"] = p.propertyType.ToString();
                    if (p.isArray)
                    {
                        j["size"] = p.arraySize;
                        if (p.propertyType == SerializedPropertyType.Generic)
                            j["children"] = ChildCount(p);
                    }
                    return j;
            }
        }

        private static int ChildCount(SerializedProperty p)
        {
            int n = 0;
            var it = p.Copy();
            var end = p.Copy();
            end.Next(false);
            while (it.Next(true) && it.propertyPath != end.propertyPath) n++;
            return n;
        }

        // ---------- write side ----------

        [McpTool("edit_transform",
            "Set local position/rotation(euler)/scale on a game object. " +
            "Params: {\"instanceId\":123,\"position\":[x,y,z],\"rotation\":[x,y,z],\"scale\":[x,y,z]}")]
        public static string EditTransform(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<TransformArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);
            if (a.position == null && a.rotation == null && a.scale == null)
                throw new McpToolException("at least one of position/rotation/scale must be provided");

            var t = go.transform;
            Undo.SetCurrentGroupName("MCP edit_transform");
            Undo.RecordObject(t, "MCP edit_transform");
            if (a.position != null) t.localPosition = EditValidation.Vec3(a.position, "position");
            if (a.rotation != null) t.localEulerAngles = EditValidation.Vec3(a.rotation, "rotation");
            if (a.scale != null) t.localScale = EditValidation.Vec3(a.scale, "scale");
            EditorUtility.SetDirty(go);

            var r = new JObject();
            r["position"] = new JArray(t.localPosition.x, t.localPosition.y, t.localPosition.z);
            var e = t.localEulerAngles;
            r["rotation"] = new JArray(e.x, e.y, e.z);
            r["scale"] = new JArray(t.localScale.x, t.localScale.y, t.localScale.z);
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("edit_set_active", "Set activeSelf on a game object. Params: {\"instanceId\":123,\"active\":false}")]
        public static string EditSetActive(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<ActiveArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);
            Undo.SetCurrentGroupName("MCP edit_set_active");
            Undo.RecordObject(go, "MCP edit_set_active");
            go.SetActive(a.active);
            EditorUtility.SetDirty(go);
                        var r = new JObject();
            r["instanceId"] = go.GetInstanceID();
            r["active"] = go.activeSelf;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("edit_set_name", "Rename a game object. Params: {\"instanceId\":123,\"name\":\"NewName\"}")]
        public static string EditSetName(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<NameArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);
            if (string.IsNullOrEmpty(a.name)) throw new McpToolException("name must not be empty");
            Undo.SetCurrentGroupName("MCP edit_set_name");
            Undo.RecordObject(go, "MCP edit_set_name");
            go.name = a.name;
            EditorUtility.SetDirty(go);
                        var r = new JObject();
            r["instanceId"] = go.GetInstanceID();
            r["name"] = go.name;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("edit_add_component",
            "Add a component by type name (e.g. BoxCollider, Rigidbody, or full type name). " +
            "Params: {\"instanceId\":123,\"componentType\":\"BoxCollider\"}")]
        public static string EditAddComponent(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<AddComponentArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);
            if (string.IsNullOrEmpty(a.componentType))
                throw new McpToolException("componentType must not be empty");
            var type = EditValidation.ResolveType(a.componentType);
            EditValidation.AssertAddableComponent(type, go);

            Undo.SetCurrentGroupName("MCP edit_add_component");
            var added = Undo.AddComponent(go, type);
            EditorUtility.SetDirty(go);
                        var r = new JObject();
            r["instanceId"] = added.GetInstanceID();
            r["type"] = added.GetType().Name;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("edit_remove_component",
            "Remove a component. Params: {\"instanceId\":123,\"componentType\":\"BoxCollider\"} " +
            "or {\"componentInstanceId\":456} (preferred).")]
        public static string EditRemoveComponent(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<RemoveComponentArgs>(argsJson ?? "{}");
            Component target = null;
            if (a.componentInstanceId != 0)
            {
                target = EditorUtility.InstanceIDToObject(a.componentInstanceId) as Component;
                if (target == null)
                    throw new McpToolException($"no component with instanceId {a.componentInstanceId}");
            }
            else if (a.instanceId != 0 && !string.IsNullOrEmpty(a.componentType))
            {
                var go = EditValidation.ResolveGameObject(a.instanceId);
                var type = EditValidation.ResolveType(a.componentType);
                target = go.GetComponent(type);
                if (target == null)
                    throw new McpToolException($"component {a.componentType} not found on object {go.name}");
            }
            else
            {
                throw new McpToolException("provide componentInstanceId, or instanceId + componentType");
            }

            Undo.SetCurrentGroupName("MCP edit_remove_component");
            var typeName = target.GetType().Name;
            var goId = target.gameObject.GetInstanceID();
            Undo.DestroyObjectImmediate(target);
            EditorUtility.SetDirty(EditorUtility.InstanceIDToObject(goId));
                        var r = new JObject();
            r["removed"] = typeName;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("edit_set_component_property",
            "Set one serialized property by propertyPath (get paths from get_object_details). " +
            "Params: {\"instanceId\":123,\"componentType\":\"Transform\",\"property\":\"m_LocalScale.x\",\"value\":2} " +
            "value types: number, string, bool, [x,y,z] vector3, [r,g,b,a] color, asset path for object refs.")]
        public static string EditSetComponentProperty(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<PropertyArgs>(argsJson ?? "{}");
            var go = EditValidation.ResolveGameObject(a.instanceId);
            if (string.IsNullOrEmpty(a.componentType) || string.IsNullOrEmpty(a.property) || string.IsNullOrEmpty(a.value))
                throw new McpToolException("componentType, property and value are required");

            var type = EditValidation.ResolveType(a.componentType);
            var component = go.GetComponent(type);
            if (component == null)
                throw new McpToolException($"component {a.componentType} not found on object {go.name}");

            var so = new SerializedObject(component);
            var p = so.FindProperty(a.property);
            if (p == null)
                throw new McpToolException($"property '{a.property}' not found on {a.componentType}");

            var parsed = EditValidation.ParseValue(a.value, p.propertyType);
            Undo.SetCurrentGroupName("MCP edit_set_component_property");
            Undo.RecordObject(component, "MCP edit_set_component_property");
            EditValidation.AssignParsed(p, parsed);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);

            var r = new JObject();
            r["property"] = p.propertyPath;
            r["value"] = WritePropertyValue(p);
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("save_scene", "Save the active scene to disk. Params: {}")]
        public static string SaveScene(string argsJson, McpToolContext ctx)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.isLoaded || string.IsNullOrEmpty(scene.path))
                throw new McpToolException("no scene is open/loaded to save");
            EditorSceneManager.SaveScene(scene);
                        var r = new JObject();
            r["saved"] = scene.path;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ---------- arg classes ----------

        [Serializable]
        private class DetailsArgs { public int instanceId; public int maxProperties; }

        [Serializable]
        private class TransformArgs { public int instanceId; public float[] position; public float[] rotation; public float[] scale; }

        [Serializable]
        private class ActiveArgs { public int instanceId; public bool active; }

        [Serializable]
        private class NameArgs { public int instanceId; public string name; }

        [Serializable]
        private class AddComponentArgs { public int instanceId; public string componentType; }

        [Serializable]
        private class RemoveComponentArgs { public int instanceId; public int componentInstanceId; public string componentType; }

        [Serializable]
        private class PropertyArgs { public int instanceId; public string componentType; public string property; public string value; }
    }

    /// <summary>Write-validation layer: id/type/value sanity checks before any mutation.</summary>
    public static class EditValidation
    {
        private static readonly Dictionary<string, Type> TypeLookup = new Dictionary<string, Type>();

        public static GameObject ResolveGameObject(int instanceId)
        {
            if (instanceId == 0)
                throw new McpToolException("instanceId must not be 0");
            var o = EditorUtility.InstanceIDToObject(instanceId);
            if (o == null)
                throw new McpToolException($"no object with instanceId {instanceId} (stale id or destroyed object)");
            var go = o as GameObject;
            if (go == null)
                throw new McpToolException($"instanceId {instanceId} is a {o.GetType().Name}, not a GameObject");
            return go;
        }

        public static Vector3 Vec3(float[] v, string name)
        {
            if (v == null || v.Length != 3)
                throw new McpToolException($"{name} must be an array of 3 numbers");
            for (int i = 0; i < 3; i++)
                if (float.IsNaN(v[i]) || float.IsInfinity(v[i]))
                    throw new McpToolException($"{name}[{i}] is not a finite number");
            return new Vector3(v[0], v[1], v[2]);
        }

        public static Type ResolveType(string typeName)
        {
            if (TypeLookup.TryGetValue(typeName, out var cached)) return cached;

            Type t = Type.GetType(typeName, false);
            if (t == null)
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        t = asm.GetType(typeName, false);
                        if (t != null) break;
                        foreach (var candidate in asm.GetTypes())
                        {
                            if (candidate.Name == typeName && typeof(Component).IsAssignableFrom(candidate))
                            {
                                t = candidate;
                                break;
                            }
                        }
                    }
                    catch { /* skip assemblies that cannot be inspected */ }
                    if (t != null) break;
                }
            if (t == null)
                throw new McpToolException($"cannot resolve component type '{typeName}'");
            TypeLookup[typeName] = t;
            return t;
        }

        public static void AssertAddableComponent(Type type, GameObject go)
        {
            if (!typeof(Component).IsAssignableFrom(type))
                throw new McpToolException($"{type.Name} is not a Component type");
            if (type.IsAbstract)
                throw new McpToolException($"{type.Name} is abstract");
            if (type == typeof(Transform) || type == typeof(RectTransform))
                throw new McpToolException($"{type.Name} cannot be added (always present)");
            if (go.GetComponent(type) != null)
                throw new McpToolException($"{type.Name} already exists on object (Unity does not allow duplicates)");
        }

        // ---------- generic property value parsing ----------

        public sealed class ParsedValue
        {
            public bool IsNumber;
            public double Number;
            public string Str;
            public bool IsBool;
            public bool Bool;
            public float[] Array;
            public string AssetPath;
        }

        public static ParsedValue ParseValue(string raw, SerializedPropertyType target)
        {
            string s = raw.Trim();
            if (s == "null") throw new McpToolException("null value not supported here");
            var v = new ParsedValue();

            if (s.StartsWith("[") && s.EndsWith("]"))
            {
                var parts = s.Substring(1, s.Length - 2).Split(',');
                v.Array = new float[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!float.TryParse(parts[i].Trim(), out var f) || float.IsNaN(f) || float.IsInfinity(f))
                        throw new McpToolException($"invalid number '{parts[i].Trim()}' in array");
                    v.Array[i] = f;
                }
            }
            else if (s == "true" || s == "false")
            {
                v.IsBool = true;
                v.Bool = s == "true";
            }
            else if (s.StartsWith("\""))
            {
                if (!s.EndsWith("\"") || s.Length < 2)
                    throw new McpToolException("malformed string value");
                v.Str = s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            else if (double.TryParse(s, out var num))
            {
                v.IsNumber = true;
                v.Number = num;
            }
            else
            {
                throw new McpToolException($"cannot parse value '{raw}'");
            }
            return v;
        }

        public static void AssignParsed(SerializedProperty p, ParsedValue v)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Float:
                    if (!v.IsNumber) throw new McpToolException("expected a number");
                    p.floatValue = (float)v.Number;
                    break;
                case SerializedPropertyType.Integer:
                    if (!v.IsNumber) throw new McpToolException("expected a number");
                    p.longValue = (long)v.Number;
                    break;
                case SerializedPropertyType.Boolean:
                    if (!v.IsBool) throw new McpToolException("expected true or false");
                    p.boolValue = v.Bool;
                    break;
                case SerializedPropertyType.String:
                    if (v.Str == null) throw new McpToolException("expected a string");
                    p.stringValue = v.Str;
                    break;
                case SerializedPropertyType.Enum:
                    if (!v.IsNumber) throw new McpToolException("expected an integer enum value");
                    p.enumValueIndex = (int)v.Number;
                    break;
                case SerializedPropertyType.Vector2:
                    p.vector2Value = Arr2(v);
                    break;
                case SerializedPropertyType.Vector3:
                    p.vector3Value = Arr3(v);
                    break;
                case SerializedPropertyType.Vector4:
                    p.vector4Value = Arr4(v);
                    break;
                case SerializedPropertyType.Quaternion:
                    p.quaternionValue = Quat(v);
                    break;
                case SerializedPropertyType.Color:
                    p.colorValue = ColorFrom(v);
                    break;
                case SerializedPropertyType.ObjectReference:
                    if (v.Str == null)
                        throw new McpToolException("expected an asset path string");
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(v.Str);
                    if (asset == null)
                        throw new McpToolException($"no asset at '{v.Str}'");
                    p.objectReferenceValue = asset;
                    break;
                default:
                    throw new McpToolException($"property type {p.propertyType} is not writable");
            }
        }

        private static Vector2 Arr2(ParsedValue v)
        {
            if (v.Array == null || v.Array.Length != 2)
                throw new McpToolException("expected an array of 2 numbers");
            return new Vector2(v.Array[0], v.Array[1]);
        }

        private static Vector3 Arr3(ParsedValue v)
        {
            if (v.Array == null || v.Array.Length != 3)
                throw new McpToolException("expected an array of 3 numbers");
            return new Vector3(v.Array[0], v.Array[1], v.Array[2]);
        }

        private static Vector4 Arr4(ParsedValue v)
        {
            if (v.Array == null || v.Array.Length != 4)
                throw new McpToolException("expected an array of 4 numbers");
            return new Vector4(v.Array[0], v.Array[1], v.Array[2], v.Array[3]);
        }

        private static Quaternion Quat(ParsedValue v)
        {
            if (v.Array == null || v.Array.Length != 4)
                throw new McpToolException("expected an array of 4 numbers (x,y,z,w)");
            return new Quaternion(v.Array[0], v.Array[1], v.Array[2], v.Array[3]);
        }

        private static Color ColorFrom(ParsedValue v)
        {
            if (v.Array == null || (v.Array.Length != 4 && v.Array.Length != 3))
                throw new McpToolException("expected an array of 3 or 4 numbers (r,g,b[,a])");
            return v.Array.Length == 4
                ? new Color(v.Array[0], v.Array[1], v.Array[2], v.Array[3])
                : new Color(v.Array[0], v.Array[1], v.Array[2], 1f);
        }
    }
}
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VrcMcp.Compat;
using VrcMcp.Core;
using VrcMcp.Handshake;
using Newtonsoft.Json.Linq;

namespace VrcMcp.Tools
{
    public static class CoreTools
    {
        [McpTool("ping", "Check bridge connectivity and editor state.")]
        public static string Ping(string argsJson, McpToolContext ctx)
        {
            var r = new JObject();
            r["pong"] = true;
            r["unityVersion"] = Application.unityVersion;
            r["editorTime"] = System.DateTimeOffset.Now.ToUnixTimeMilliseconds();
            r["playMode"] = EditorApplication.isPlaying;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("get_project_info", "Project identity, transport, compat status, VRCSDK presence and batch state.")]
        public static string GetProjectInfo(string argsJson, McpToolContext ctx)
        {
            var r = new JObject();
            r["projectName"] = Application.productName;
            r["projectPath"] = ChannelFile.ProjectDir;
            r["unityVersion"] = Application.unityVersion;
            var tr = new JObject();
            tr["name"] = BridgeStatus.Name;
            tr["port"] = BridgeStatus.Port;
            tr["running"] = BridgeStatus.Running;
            tr["status"] = BridgeStatus.Status;
            r["transport"] = tr;
            var sdkIssues = VrcSdkDetector.GetHealthIssues();
            var health = new JObject();
            health["status"] = VrcSdkDetector.IsInstalled ? (sdkIssues.Count == 0 ? "ok" : "broken") : "absent";
            health["baseDir"] = VrcSdkDetector.BasePackageDir;
            var iss = new JArray();
            foreach (var s in sdkIssues) iss.Add(s);
            health["issues"] = iss;
            r["sdkHealth"] = health;
            r["avatarImportAvailable"] = VrcSdkDetector.IsInstalled && sdkIssues.Count == 0;
            r["vrcSdkVersion"] = VrcSdkDetector.Version;
            r["batch"] = JObject.Parse(BatchTools.BatchStateJson(ctx));
            var compat = new JArray();
            foreach (var s in InternalApiRegistry.GetStatus())
            {
                var jc = new JObject();
                jc["key"] = s.Key;
                jc["available"] = s.Available;
                jc["description"] = s.Description;
                jc["reason"] = s.FailureReason ?? "";
                compat.Add(jc);
            }
            r["compat"] = compat;
            r["channelFile"] = ChannelFile.ActivePath ?? "";
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("get_scene_hierarchy",
            "Serialized scene tree with instanceIds, components and transforms. " +
            "Params: {\"maxDepth\":8,\"maxNodes\":2000}")]
        public static string GetSceneHierarchy(string argsJson, McpToolContext ctx)
        {
            int maxDepth = JsonArg(argsJson, "maxDepth", 8);
            int maxNodes = JsonArg(argsJson, "maxNodes", 2000);

            var scene = SceneManager.GetActiveScene();
            var root = new JObject();
            root["sceneName"] = scene.name;
            root["scenePath"] = scene.path;
            root["isDirty"] = scene.isDirty;
            var roots = scene.GetRootGameObjects();
            root["rootCount"] = roots.Length;
            int budget = maxNodes;
            var arr = new JArray();
            for (int i = 0; i < roots.Length && budget > 0; i++)
                arr.Add(WriteNode(roots[i].transform, 0, maxDepth, ref budget));
            root["roots"] = arr;
            root["truncated"] = budget <= 0;
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JObject WriteNode(Transform t, int depth, int maxDepth, ref int budget)
        {
            if (budget <= 0) return null;
            budget--;
            var n = new JObject();
            n["instanceId"] = t.gameObject.GetInstanceID();
            n["name"] = t.gameObject.name;
            n["active"] = t.gameObject.activeSelf;
            n["layer"] = t.gameObject.layer;
            n["tag"] = t.gameObject.tag;
            n["position"] = new JArray(t.localPosition.x, t.localPosition.y, t.localPosition.z);
            var euler = t.localEulerAngles;
            n["rotation"] = new JArray(euler.x, euler.y, euler.z);
            n["scale"] = new JArray(t.localScale.x, t.localScale.y, t.localScale.z);
            n["childCount"] = t.childCount;
            var components = t.gameObject.GetComponents<Component>();
            var comps = new JArray();
            foreach (var c in components)
            {
                var jc = new JObject();
                jc["type"] = c != null ? c.GetType().Name : "missing";
                jc["instanceId"] = c != null ? c.GetInstanceID() : 0;
                var b = c as Behaviour;
                jc["enabled"] = b != null ? (JToken)(b.enabled ? true : false) : null;
                comps.Add(jc);
            }
            n["components"] = comps;
            if (depth < maxDepth && t.childCount > 0)
            {
                var children = new JArray();
                for (int i = 0; i < t.childCount && budget > 0; i++)
                {
                    var child = WriteNode(t.GetChild(i), depth + 1, maxDepth, ref budget);
                    if (child != null) children.Add(child);
                }
                n["children"] = children;
            }
            return n;
        }

        [McpTool("get_selection", "Currently selected objects in the editor.")]
        public static string GetSelection(string argsJson, McpToolContext ctx)
        {
            var r = new JObject();
            var gos = new JArray();
            foreach (var go in Selection.gameObjects)
            {
                var jg = new JObject();
                jg["instanceId"] = go.GetInstanceID();
                jg["name"] = go.name;
                gos.Add(jg);
            }
            r["gameObjects"] = gos;
            var assets = new JArray();
            foreach (var guid in Selection.assetGUIDs)
            {
                var ja = new JObject();
                ja["guid"] = guid;
                ja["path"] = AssetDatabase.GUIDToAssetPath(guid);
                assets.Add(ja);
            }
            r["assets"] = assets;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("diagnostic_bad_json",
            "DIAGNOSTIC ONLY. Deliberately returns malformed JSON: an unquoted string value " +
            "('from'/'to'), reproducing the 禮21/禮24 bug class (hand-built JSON missing quotes). " +
            "The Python side MUST reject it with json.loads JSONDecodeError; regression asserts " +
            "that the safety net catches it. Test-only, no side effects.")]
        public static string DiagnosticBadJson(string argsJson, McpToolContext ctx)
        {
            return "{\"from\":Idle,\"to\":Run}";
        }

        private static int JsonArg(string argsJson, string key, int def)
        {
            try
            {
                int idx = argsJson.IndexOf('"' + key + '"', System.StringComparison.Ordinal);
                if (idx < 0) return def;
                int colon = argsJson.IndexOf(':', idx + key.Length + 2);
                if (colon < 0) return def;
                int end = colon + 1;
                while (end < argsJson.Length && (argsJson[end] == ' ' || argsJson[end] == '\t')) end++;
                int start = end;
                while (end < argsJson.Length && (char.IsDigit(argsJson[end]) || argsJson[end] == '-')) end++;
                if (end == start) return def;
                return int.Parse(argsJson.Substring(start, end - start));
            }
            catch { return def; }
        }
    }

    /// <summary>Status accessor decoupling tools from the bootstrap singleton.</summary>
    public static class BridgeStatus
    {
        public static string Name => Bootstrap.BridgeBootstrap.Transport?.Name ?? "none";
        public static int Port => Bootstrap.BridgeBootstrap.Transport?.Port ?? -1;
        public static bool Running => Bootstrap.BridgeBootstrap.IsRunning;
        public static string Status => Bootstrap.BridgeBootstrap.Transport?.Status ?? "not started";
    }

    /// <summary>
    /// VRCSDK presence detection via the project package manifest + health check for
    /// known broken official-package files (DESIGN 禮26: 3.10.x ships two unguarded
    /// test files that break VRC.SDKBase.Editor compilation; a VCC re-sync restores
    /// them). Health is checked live on every call - the package dir is outside our
    /// control and can be reset by VCC at any time.
    /// </summary>
    public static class VrcSdkDetector
    {
        public static bool IsInstalled => !string.IsNullOrEmpty(Version);
        public static string Version { get; private set; } = "";
        public static string BasePackageDir { get; private set; } = "";

        /// <summary>Official-package test files known to break the VRC.SDKBase.Editor
        /// assembly (no #if guard, no test asmdef; nunit.framework.dll is explicitly
        /// referenced so it is invisible to normal asmdefs).</summary>
        public static readonly string[] KnownBrokenTestFiles =
        {
            "Editor/VRCSDK/VTP/VTPTests.cs",
            "Editor/VRCSDK/Dependencies/VRChat/Tests/AssetBundleFooterTest.cs",
        };

        static VrcSdkDetector()
        {
            try
            {
                var manifestPath = Path.Combine(ChannelFile.ProjectDir, "Packages", "manifest.json");
                if (!File.Exists(manifestPath)) return;
                var json = File.ReadAllText(manifestPath);
                Version = ParseManifestValue(json, "com.vrchat.avatars");
                BasePackageDir = ResolveBaseDir(json);
            }
            catch { }
        }

        /// <summary>Live check: any known broken test file present in the base package?</summary>
        public static List<string> GetHealthIssues()
        {
            var issues = new List<string>();
            if (string.IsNullOrEmpty(BasePackageDir)) return issues;
            foreach (var rel in KnownBrokenTestFiles)
            {
                var p = Path.Combine(BasePackageDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p))
                    issues.Add($"official SDK test file present: {rel} (breaks VRC.SDKBase.Editor compile; run sdk_repair_test_files)");
            }
            return issues;
        }

        private static string ParseManifestValue(string json, string key)
        {
            int idx = json.IndexOf(key, System.StringComparison.Ordinal);
            if (idx < 0) return "";
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return "";
            int start = json.IndexOf('"', colon + 1);
            if (start < 0) return "";
            int end = json.IndexOf('"', start + 1);
            if (end < 0) return "";
            return json.Substring(start + 1, end - start - 1);
        }

        private static string ResolveBaseDir(string json)
        {
            var val = ParseManifestValue(json, "com.vrchat.base");
            if (string.IsNullOrEmpty(val)) return "";
            if (val.StartsWith("file:", System.StringComparison.OrdinalIgnoreCase))
            {
                var p = val.Substring(5).Trim();
                if (p.StartsWith("//", System.StringComparison.Ordinal)) p = p.Substring(2);
                return p.Replace('/', Path.DirectorySeparatorChar);
            }
            var embedded = Path.Combine(ChannelFile.ProjectDir, "Packages", "com.vrchat.base");
            if (Directory.Exists(embedded)) return embedded;
            var cacheRoot = Path.Combine(ChannelFile.ProjectDir, "Library", "PackageCache");
            if (Directory.Exists(cacheRoot))
            {
                var hits = Directory.GetDirectories(cacheRoot, "com.vrchat.base-*");
                if (hits.Length > 0) return hits[0];
            }
            return "";
        }
    }
}
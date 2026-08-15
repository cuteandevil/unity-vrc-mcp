using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Newtonsoft.Json.Linq;
using VrcMcp.Core;
using VrcMcp.Handshake;

namespace VrcMcp.Tools
{
    /// <summary>
    /// Phase 3 import tools. asset_import_fbx copies an FBX/OBJ/GLB into the project,
    /// imports it via AssetDatabase and returns the resulting asset's root GameObject.
    /// import_avatar_from_zip is the one-shot avatar pipeline: unzip (safe) -> find the
    /// first model file -> import -> instantiate in a new scene.
    /// All writes go through undo-able paths where possible; AssetDatabase import itself
    /// is not undoable, so import tools MUST be used OUTSIDE a batch (or the batch will
    /// not be fully undoable - the imported asset is a project asset, not scene state).
    /// </summary>
    public static class ImportTools
    {
        private static readonly string[] kModelExts = { ".fbx", ".obj", ".glb", ".gltf", ".dae", ".blend" };

        private static readonly string[] kAvatarRootPatterns =
        {
            "Armature", "Hips", "Bone", "Avatar", "Root"
        };

        // ---------- missing-shader detection (data-driven family signatures) ----------
        // A material whose shader is missing renders magenta/pink in Unity. The material
        // FILE still holds the original serialized property names (m_TexEnvs/m_Floats),
        // which identify the shader family (lilToon/Poiyomi/...) even though the shader
        // itself is gone - the runtime Material API cannot do this because the missing
        // shader is replaced by Hidden/InternalErrorShader which has no custom properties.
        // Signature table: family name -> distinguishing property names (lilToon-only props
        // like _Anisotropy* / _AlphaMask; extend with Poiyomi etc. when encountered).
        private static readonly Dictionary<string, string[]> kShaderFamilySignatures =
            new Dictionary<string, string[]>
            {
                {
                    "lilToon",
                    new[]
                    {
                        "_AlphaMask", "_AnisotropyScaleMask", "_AnisotropyShiftNoiseMask",
                        "_AnisotropyTangentMap", "_Main2ndTex", "_Main3rdTex",
                        "_OutlineWidth", "_RimColor", "_TriMask", "_EmissionMap"
                    }
                }
            };

        private static bool IsShaderMissing(Material m)
        {
            if (m == null) return true;
            var s = m.shader;
            return s == null || s.name == "Hidden/InternalErrorShader";
        }

        private static string DetectShaderFamilyFromMatText(string text)
        {
            foreach (var kv in kShaderFamilySignatures)
                foreach (var prop in kv.Value)
                    if (text.IndexOf("- " + prop + ":", StringComparison.Ordinal) >= 0)
                        return kv.Key;
            return null;
        }

        /// <summary>Scan .mat asset paths for materials whose shader failed to resolve.
        /// Reads the material file text for family inference (see comment above).
        /// Returns one needsAttention message per family found, plus a generic one
        /// for families we cannot infer. Never touches the materials themselves.</summary>
        private static List<string> ScanMissingShaders(IEnumerable<string> matPaths)
        {
            var byFamily = new Dictionary<string, List<string>>();
            var unknown = new List<string>();
            foreach (var p in matPaths)
            {
                var abs = Path.Combine(ChannelFile.ProjectDir, p.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs)) continue;
                var mat = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (!IsShaderMissing(mat)) continue;
                var text = File.ReadAllText(abs);
                var fam = DetectShaderFamilyFromMatText(text);
                var name = Path.GetFileName(p);
                if (fam != null)
                {
                    if (!byFamily.TryGetValue(fam, out var lst))
                    {
                        lst = new List<string>();
                        byFamily[fam] = lst;
                    }
                    lst.Add(name);
                }
                else
                {
                    unknown.Add(name);
                }
            }
            var msgs = new List<string>();
            foreach (var kv in byFamily)
                msgs.Add($"{kv.Key} shader missing on {kv.Value.Count} material(s) ({string.Join(", ", kv.Value)}): the package references {kv.Key} but it is not installed - install it via VCC and re-import (materials currently render magenta)");
            if (unknown.Count > 0)
                msgs.Add($"{unknown.Count} material(s) ({string.Join(", ", unknown)}) reference an uninstalled shader - likely a third-party shader like lilToon/Poiyomi; install it via VCC and re-import (materials currently render magenta)");
            return msgs;
        }

        /// <summary>Scene-level fallback for embedded materials (FBX/zip imports have no
        /// standalone .mat text to infer the family from, so this only reports generically).</summary>
        private static List<string> ScanMissingShadersInGameObject(GameObject go)
        {
            var seen = new HashSet<Material>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && !seen.Contains(m) && IsShaderMissing(m))
                        seen.Add(m);
            if (seen.Count == 0) return new List<string>();
            return new List<string>
            {
                $"{seen.Count} material(s) on '{go.name}' reference an uninstalled shader - likely a third-party shader like lilToon/Poiyomi; install it via VCC and re-import (materials currently render magenta)"
            };
        }

        /// <summary>Scan missing shaders, then auto-install whitelisted families (DESIGN §33).
        /// Families installed successfully drop out of the report (their shaders now resolve);
        /// failures keep the needsAttention message with the failure reason appended.
        /// autoFixed records what was installed and from where.</summary>
        private static List<string> ScanMissingShadersWithAutoInstall(IEnumerable<string> matPaths, JArray autoFixed)
        {
            var msgs = ScanMissingShaders(matPaths);
            var families = new HashSet<string>();
            foreach (var msg in msgs)
            {
                var fam = ParseFamilyFromMessage(msg);
                if (fam != null) families.Add(fam);
            }
            foreach (var fam in families)
            {
                string version, source, error;
                var status = ShaderInstallTools.TryAutoInstall(fam, null, out version, out source, out error);
                if (status == ShaderInstallTools.InstallStatus.Installed)
                    autoFixed.Add($"auto-installed {fam} {version} from {source}");
                else if (status == ShaderInstallTools.InstallStatus.Failed)
                    msgs.Add($"[auto-install failed for {fam}: {error}]");
            }
            // Re-scan: families installed above no longer report (their shader guids resolve).
            return ScanMissingShaders(matPaths).Concat(
                msgs.Where(m => m.StartsWith("[auto-install failed", StringComparison.Ordinal))).ToList();
        }

        /// <summary>Parse "lilToon shader missing on N material(s)" -> "lilToon" (null for generic).</summary>
        private static string ParseFamilyFromMessage(string msg)
        {
            var idx = msg.IndexOf(" shader missing", StringComparison.Ordinal);
            if (idx <= 0) return null;
            var fam = msg.Substring(0, idx);
            if (fam.IndexOf(' ') >= 0 || fam.IndexOf('[') >= 0) return null;
            return fam;
        }

        [McpTool("asset_import_fbx",
            "Copy a local model file (FBX/OBJ/GLB/GLTF/DAE/BLEND) into the project Assets " +
            "folder and import it via AssetDatabase. " +
            "Params: {\"sourcePath\":\"C:/x/model.fbx\",\"destDir\":\"Assets/Imports\"}. " +
            "destDir defaults to Assets/Imports. Returns the imported asset path and " +
            "whether it contains an Avatar. NOT undoable (project asset) - use outside a batch.")]
        public static string AssetImportFbx(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<ImportFbxArgs>(argsJson ?? "{}");
            if (string.IsNullOrEmpty(a.sourcePath) || !File.Exists(a.sourcePath))
                throw new McpToolException($"sourcePath does not exist: {a.sourcePath}");
            var ext = Path.GetExtension(a.sourcePath).ToLowerInvariant();
            if (Array.IndexOf(kModelExts, ext) < 0)
                throw new McpToolException($"unsupported model extension: {ext} (supported: {string.Join(",", kModelExts)})");

            if (string.IsNullOrEmpty(a.destDir))
                a.destDir = "Assets/Imports";
            a.destDir = a.destDir.Replace('\\', '/');
            if (!a.destDir.StartsWith("Assets/", StringComparison.Ordinal))
                throw new McpToolException("destDir must live under Assets/");

            EnsureFolder(a.destDir);

            var fileName = Path.GetFileName(a.sourcePath);
            var destPath = a.destDir + "/" + fileName;
            var candidate = destPath;
            var i = 1;
            while (File.Exists(Path.Combine(ChannelFile.ProjectDir, candidate)) || AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate) != null)
            {
                candidate = a.destDir + "/" + Path.GetFileNameWithoutExtension(fileName) + "_" + i + ext;
                i++;
            }
            destPath = candidate;

            File.Copy(a.sourcePath, Path.Combine(ChannelFile.ProjectDir, destPath));
            AssetDatabase.ImportAsset(destPath, ImportAssetOptions.ForceUpdate);

            var root = AssetDatabase.LoadMainAssetAtPath(destPath) as GameObject;
            var hasAvatar = root != null && root.GetComponent<Animator>() != null
                && root.GetComponent<Animator>().avatar != null;

            var r = new JObject();
            r["assetPath"] = destPath;
            r["hasAvatar"] = hasAvatar;
            if (root != null)
                r["rootName"] = root.name;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("import_avatar_from_zip",
            "One-shot avatar import from a local zip: safe unzip (zip-slip guarded, " +
            "byte-budget limited) into the project's AvatarImports folder, find the first " +
            "model file (.fbx/.obj/.glb/.gltf/.dae/.blend), import it via AssetDatabase, " +
            "instantiate its root into the CURRENT scene at origin (undoable). " +
            "Params: {\"zipPath\":\"C:/x/avatar.zip\",\"destDir\":\"Assets/AvatarImports\",\"import\":true}. " +
            "NOT undoable for the project asset part; the scene instantiation IS undoable. " +
            "Use OUTSIDE a batch for the import part.")]
        public static string ImportAvatarFromZip(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<ImportZipArgs>(argsJson ?? "{}");
            if (string.IsNullOrEmpty(a.zipPath) || !File.Exists(a.zipPath))
                throw new McpToolException($"zipPath does not exist: {a.zipPath}");
            if (string.IsNullOrEmpty(a.destDir))
                a.destDir = "Assets/AvatarImports";
            a.destDir = a.destDir.Replace('\\', '/');
            if (!a.destDir.StartsWith("Assets/", StringComparison.Ordinal))
                throw new McpToolException("destDir must live under Assets/");

            EnsureFolder(a.destDir);
            var projectDir = ChannelFile.ProjectDir;
            var absDest = Path.Combine(projectDir, a.destDir.Replace('/', Path.DirectorySeparatorChar));

            var extracted = SafeUnzip(a.zipPath, absDest);

            string modelFile = null;
            var modelRel = "";
            foreach (var f in extracted)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(kModelExts, ext) >= 0)
                {
                    var rel = Path.GetRelativePath(projectDir, f).Replace('\\', '/');
                    if (rel.StartsWith("Assets/", StringComparison.Ordinal))
                        modelRel = rel;
                    else
                        modelRel = "Assets/" + rel;
                    modelFile = f;
                    break;
                }
            }
            if (modelFile == null)
                throw new McpToolException("no model file found in zip (looked for " + string.Join(",", kModelExts) + ")");

            var r = new JObject();
            r["pipelineVersion"] = 2;
            r["zipPath"] = a.zipPath;
            r["extracted"] = extracted.Count;
            r["modelPath"] = modelRel;
            if (!a.import)
                return r.ToString(Newtonsoft.Json.Formatting.None);

            AssetDatabase.ImportAsset(modelRel, ImportAssetOptions.ForceUpdate);
            var root = AssetDatabase.LoadMainAssetAtPath(modelRel) as GameObject;
            if (root == null)
                throw new McpToolException($"imported asset has no GameObject root: {modelRel}");

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("MCP import_avatar_from_zip");
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(root);
            if (inst == null)
                inst = UnityEngine.Object.Instantiate(root);
            inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Undo.RegisterCreatedObjectUndo(inst, "MCP import_avatar_from_zip instance");
            Undo.CollapseUndoOperations(group);

            // v2: validation report + auto-fix/attention lists + control panel handoff.
            var errors = new JArray();
            var autoFixed = new JArray();
            var needsAttention = new JArray();
            bool sdkPresent = SdkTools.SdkResolvable;
            if (!sdkPresent)
            {
                needsAttention.Add("VRCSDK (com.vrchat.avatars) not installed - avatar cannot be uploaded; install the SDK first");
            }
            else if (root.GetComponent(SdkTools.AvatarDescriptorType) == null)
            {
                needsAttention.Add($"no VRCAvatarDescriptor on imported root '{root.name}' - add one and configure rig/blendshapes before upload");
            }
            foreach (var msg in ScanMissingShadersInGameObject(root))
                needsAttention.Add(msg);

            bool panelOpened = false;
            if (sdkPresent)
            {
                try { panelOpened = SdkTools.OpenControlPanel(); }
                catch { }
            }

            var report = new JObject();
            bool hasDescriptor = false;
            if (sdkPresent)
                hasDescriptor = root.GetComponent(SdkTools.AvatarDescriptorType) != null;
            report["passed"] = sdkPresent && hasDescriptor && errors.Count == 0;
            report["errors"] = errors;
            r["validationReport"] = report;
            r["autoFixed"] = autoFixed;
            r["needsAttention"] = needsAttention;
            r["controlPanelOpened"] = panelOpened;
            r["instanceId"] = inst.GetInstanceID();
            r["instanceName"] = inst.name;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("import_unitypackage",
            "Import a .unitypackage asset package into the project via AssetDatabase.ImportPackage " +
            "(non-interactive). This is the standard distribution format for VRChat avatars - it " +
            "preserves prefab assembly, GUID references, animation controllers, expression menus, " +
            "PhysBones and materials. Params: {\"packagePath\":\"C:/x/avatar.unitypackage\"}. " +
            "Returns imported asset paths, detected prefabs/models and a validation report " +
            "(VRCAvatarDescriptor presence, SDK health). NOT undoable (project assets) - use outside a batch.")]
        public static string ImportUnityPackage(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<ImportPackageArgs>(argsJson ?? "{}");
            if (string.IsNullOrEmpty(a.packagePath) || !File.Exists(a.packagePath))
                throw new McpToolException($"packagePath does not exist: {a.packagePath}");
            if (!a.packagePath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException("packagePath must be a .unitypackage file");

            var imported = ExtractUnityPackage(a.packagePath, ChannelFile.ProjectDir);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var prefabs = imported.Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)).ToList();
            var models = imported.Where(p =>
            {
                var ext = Path.GetExtension(p).ToLowerInvariant();
                return Array.IndexOf(kModelExts, ext) >= 0;
            }).ToList();

            var r = new JObject();
            r["packagePath"] = a.packagePath;
            r["importedCount"] = imported.Count;
            r["importedAssets"] = new JArray(imported.Select(p => (JToken)p));
            r["prefabs"] = new JArray(prefabs.Select(p => (JToken)p));
            r["models"] = new JArray(models.Select(p => (JToken)p));

            bool sdkPresent = SdkTools.SdkResolvable;
            var needsAttention = new JArray();
            var autoFixed = new JArray();
            if (!sdkPresent)
                needsAttention.Add("VRCSDK (com.vrchat.avatars) not installed - avatar cannot be uploaded; install the SDK first");

            string primaryPrefab = null;
            bool hasDescriptor = false;
            if (prefabs.Count > 0)
            {
                // Pick the "main" avatar prefab: prefer one carrying a VRCAvatarDescriptor,
                // and skip clearly-optional prefabs (names containing Optional/MA suffix).
                var candidates = prefabs
                    .Where(p => p.IndexOf("Optional", StringComparison.OrdinalIgnoreCase) < 0)
                    .ToList();
                if (candidates.Count == 0) candidates = prefabs;
                foreach (var p in candidates)
                {
                    primaryPrefab = p;
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                    if (go == null) continue;
                    if (sdkPresent && go.GetComponent(SdkTools.AvatarDescriptorType) != null)
                    {
                        hasDescriptor = true;
                        break;
                    }
                }
                if (!hasDescriptor)
                    needsAttention.Add($"no VRCAvatarDescriptor on primary prefab '{primaryPrefab}' - add one and configure rig/blendshapes before upload");
            }
            else if (models.Count == 0)
            {
                needsAttention.Add("no prefab or model file found in package");
            }

            var matPaths = imported.Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var msg in ScanMissingShadersWithAutoInstall(matPaths, autoFixed))
                needsAttention.Add(msg);

            var report = new JObject();
            report["passed"] = sdkPresent && hasDescriptor;
            report["primaryPrefab"] = primaryPrefab;
            report["errors"] = new JArray();
            r["validationReport"] = report;
            r["autoFixed"] = autoFixed;
            r["needsAttention"] = needsAttention;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ---------- helpers ----------

        /// <summary>
        /// unitypackage = gzip-compressed tar. Each entry is a GUID-named directory
        /// containing asset, asset.meta, pathname (target path), preview.png.
        /// Two-pass extract: pass 1 collects guid-&gt;target path from every pathname
        /// entry; pass 2 writes asset + asset.meta bytes to the project, preserving
        /// GUID references exactly like a native import. Fully synchronous.
        /// </summary>
        private static List<string> ExtractUnityPackage(string packagePath, string projectDir)
        {
            var guidToPath = new Dictionary<string, string>();
            // pass 1: pathname entries
            foreach (var kv in ReadTarEntries(packagePath))
            {
                if (!kv.Key.EndsWith("/pathname", StringComparison.Ordinal)) continue;
                var guid = kv.Key.Substring(0, kv.Key.IndexOf('/'));
                var rel = System.Text.Encoding.UTF8.GetString(kv.Value).Trim().Replace('\\', '/');
                if (rel.StartsWith("Assets/", StringComparison.Ordinal))
                    guidToPath[guid] = rel;
            }

            // pass 2: asset + asset.meta writes
            var written = new List<string>();
            foreach (var kv in ReadTarEntries(packagePath))
            {
                if (kv.Value == null || kv.Value.Length == 0) continue;
                var slash = kv.Key.IndexOf('/');
                if (slash < 0) continue;
                var guid = kv.Key.Substring(0, slash);
                if (!guidToPath.TryGetValue(guid, out var rel)) continue;
                var dest = Path.Combine(projectDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (kv.Key.EndsWith("/asset", StringComparison.Ordinal))
                {
                    File.WriteAllBytes(dest, kv.Value);
                    written.Add(rel);
                }
                else if (kv.Key.EndsWith("/asset.meta", StringComparison.Ordinal))
                {
                    File.WriteAllBytes(dest + ".meta", kv.Value);
                }
            }
            return written;
        }

        /// <summary>Read all tar entries as (name, body) pairs; body may be null for directories.</summary>
        private static List<KeyValuePair<string, byte[]>> ReadTarEntries(string packagePath)
        {
            var result = new List<KeyValuePair<string, byte[]>>();
            try
            {
                using (var fs = File.OpenRead(packagePath))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                {
                    var header = new byte[512];
                    while (ReadFull(gz, header, 512))
                    {
                        // 512-byte tar header; name at 0..99, size (octal) at 124..135.
                        var name = System.Text.Encoding.UTF8.GetString(header, 0, 100).TrimEnd('\0').Trim();
                        if (name.Length == 0) break;
                        var sizeStr = System.Text.Encoding.ASCII.GetString(header, 124, 12).TrimEnd('\0', ' ').Trim();
                        long size = 0;
                        if (sizeStr.Length > 0)
                        {
                            try { size = Convert.ToInt64(sizeStr, 8); }
                            catch { size = 0; }
                        }
                        byte[] body = null;
                        if (size > 0 && size <= int.MaxValue)
                        {
                            body = new byte[size];
                            if (!ReadFull(gz, body, (int)size)) return result;
                        }
                        result.Add(new KeyValuePair<string, byte[]>(name, body));
                        // skip padding to 512 boundary
                        long remaining = ((size + 511) / 512 * 512) - size;
                        var skipBuf = new byte[4096];
                        while (remaining > 0)
                        {
                            int want = (int)Math.Min(remaining, skipBuf.Length);
                            if (!ReadFull(gz, skipBuf, want)) return result;
                            remaining -= want;
                        }
                    }
                }
            }
            catch (Exception) { }
            return result;
        }

        private static bool ReadFull(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        private static void EnsureFolder(string assetsPath)
        {
            var parts = assetsPath.Split('/');
            var cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                if (!AssetDatabase.IsValidFolder(cur + "/" + parts[i]))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur += "/" + parts[i];
            }
        }

        /// <summary>Safe unzip: zip-slip guarded, byte-budget limited, entry-count limited.
        /// Internal so the shader auto-installer reuses the same guarded path (DESIGN §33).</summary>
        internal static List<string> SafeUnzip(string zipPath, string absDest)
        {
            const long kMaxTotalBytes = 1L << 31;   // 2 GiB budget
            const int kMaxEntries = 8192;
            var result = new List<string>();
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.Entries.Count > kMaxEntries)
                    throw new McpToolException($"zip has too many entries: {zip.Entries.Count} > {kMaxEntries}");
                long total = 0;
                Directory.CreateDirectory(absDest);
                foreach (var entry in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(absDest, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(absDest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !target.Equals(absDest, StringComparison.OrdinalIgnoreCase))
                        throw new McpToolException($"zip-slip blocked: {entry.FullName}");
                    if (entry.Length > 0)
                    {
                        total += entry.Length;
                        if (total > kMaxTotalBytes)
                            throw new McpToolException($"zip exceeds byte budget ({kMaxTotalBytes} bytes)");
                    }
                    if (entry.Name.Length == 0)
                        continue; // directory entry
                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);
                    entry.ExtractToFile(target, overwrite: true);
                    result.Add(target);
                }
            }
            return result;
        }

        [Serializable]
        private class ImportFbxArgs { public string sourcePath; public string destDir; }

        [Serializable]
        private class ImportZipArgs { public string zipPath; public string destDir; public bool import; }

        [Serializable]
        private class ImportPackageArgs { public string packagePath; }
    }
}
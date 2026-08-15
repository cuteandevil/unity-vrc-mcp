using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using VrcMcp.Core;
using VrcMcp.Handshake;

namespace VrcMcp.Tools
{
    /// <summary>
    /// Whitelisted third-party shader package installation (DESIGN §33).
    ///
    /// The one-shot avatar pipeline stops with needsAttention when materials
    /// reference an uninstalled shader family (lilToon/Poiyomi). For families in
    /// the whitelist below the pipeline can safely auto-install instead of
    /// interrupting the user: the install source is a FIXED, data-driven URL
    /// (never a runtime parameter), the downloaded zip is validated (package.json
    /// name must match the whitelisted package id) and unpacked through the SAME
    /// zip-slip/budget-guarded SafeUnzip used by import_avatar_from_zip. Anything
    /// not whitelisted stays in needsAttention - we never guess install sources.
    ///
    /// Design constraints (user decisions, 2026-08-15):
    /// - Source trust: URLs come only from kShaderInstallSources; package.json
    ///   name check guards "wrong package", SafeUnzip guards "bad bytes".
    /// - Whitelist entries are added only when a real case is encountered
    ///   (Poiyomi intentionally has no entry yet).
    /// - Failures clean up partial downloads/unpacks before reporting.
    /// - Pipeline-internal triggers call TryAutoInstall directly (no MCP
    ///   approval); the standalone MCP tool apply_shader_package_install is
    ///   approved via the existing apply_* permission prefix.
    /// </summary>
    public static class ShaderInstallTools
    {
        private sealed class ShaderInstallSource
        {
            public string VpmRepoUrl;
            public string PackageId;
        }

        /// <summary>
        /// family (as reported by ImportTools.kShaderFamilySignatures) -> install source.
        /// Poiyomi intentionally has no entry: add only when a real case appears.
        /// </summary>
        private static readonly Dictionary<string, ShaderInstallSource> kShaderInstallSources =
            new Dictionary<string, ShaderInstallSource>
            {
                {
                    "lilToon",
                    new ShaderInstallSource
                    {
                        VpmRepoUrl = "https://lilxyzw.github.io/vpm-repos/vpm.json",
                        PackageId = "jp.lilxyzw.liltoon"
                    }
                }
            };

        private const long kMaxDownloadBytes = 256L * 1024 * 1024;
        private const int kDownloadTimeoutSeconds = 30;

        public enum InstallStatus
        {
            NotInWhitelist,
            AlreadyInstalled,
            Installed,
            Failed
        }

        /// <summary>
        /// Attempt to auto-install the package for a shader family. Used by the
        /// import pipeline (no approval layer). localZipOverride bypasses the
        /// network download (offline install / regression injection); null means
        /// download from the whitelisted source.
        /// </summary>
        public static InstallStatus TryAutoInstall(string family, string localZipOverride,
            out string version, out string source, out string error)
        {
            version = null;
            source = null;
            error = null;
            if (family == null || !kShaderInstallSources.TryGetValue(family, out var src))
                return InstallStatus.NotInWhitelist;

            source = src.VpmRepoUrl;
            var pkgDir = Path.Combine(ChannelFile.ProjectDir, "Packages", src.PackageId);
            var pkgJson = Path.Combine(pkgDir, "package.json");

            if (File.Exists(pkgJson))
            {
                try
                {
                    var name = JObject.Parse(File.ReadAllText(pkgJson))["name"]?.ToString();
                    if (name == src.PackageId)
                    {
                        version = JObject.Parse(File.ReadAllText(pkgJson))["version"]?.ToString() ?? "?";
                        return InstallStatus.AlreadyInstalled;
                    }
                }
                catch { }
            }

            string zipPath = null;
            bool dirCreatedHere = false;
            try
            {
                if (!string.IsNullOrEmpty(localZipOverride))
                {
                    zipPath = localZipOverride;
                    if (!File.Exists(zipPath))
                    {
                        error = $"localZipPath does not exist: {zipPath}";
                        return InstallStatus.Failed;
                    }
                    version = "local";
                }
                else
                {
                    var url = ResolveLatestPackageUrl(src);
                    if (url == null)
                    {
                        error = $"cannot resolve latest package URL from {src.VpmRepoUrl} (package {src.PackageId})";
                        return InstallStatus.Failed;
                    }
                    zipPath = Path.Combine(Path.GetTempPath(), "vrcmcp_shader_" + Guid.NewGuid().ToString("N") + ".zip");
                    DownloadToFile(url, zipPath, out error);
                    if (zipPath == null) // download failed (error set)
                        return InstallStatus.Failed;
                    version = ParseVersionFromUrl(url);
                }

                // Validate the package BEFORE touching Packages/: package.json at
                // the zip root must declare the whitelisted package id.
                string declaredName;
                if (!TryReadZipPackageJsonName(zipPath, out declaredName, out error))
                    return InstallStatus.Failed;
                if (declaredName != src.PackageId)
                {
                    error = $"package.json name mismatch: declared '{declaredName}', expected '{src.PackageId}' - refusing to install";
                    return InstallStatus.Failed;
                }

                if (!Directory.Exists(pkgDir))
                {
                    Directory.CreateDirectory(pkgDir);
                    dirCreatedHere = true;
                }

                // Same zip-slip + byte/entry budget guards as import_avatar_from_zip.
                var written = ImportTools.SafeUnzip(zipPath, pkgDir);
                if (written.Count == 0)
                {
                    error = "zip contained no extractable files";
                    return InstallStatus.Failed;
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return InstallStatus.Installed;
            }
            catch (Exception e)
            {
                error = e.Message;
                // Roll back partial state: remove what we created, never touch a
                // pre-existing directory that was not ours.
                if (dirCreatedHere)
                {
                    try { Directory.Delete(pkgDir, recursive: true); } catch { }
                }
                return InstallStatus.Failed;
            }
            finally
            {
                if (!string.IsNullOrEmpty(localZipOverride))
                {
                    // regression-injected file is owned by the caller - keep it
                }
                else if (zipPath != null && File.Exists(zipPath))
                {
                    try { File.Delete(zipPath); } catch { }
                }
            }
        }

        /// <summary>Download a VPM repo json and resolve the latest version's package zip URL.</summary>
        private static string ResolveLatestPackageUrl(ShaderInstallSource src)
        {
            string repoJson;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(kDownloadTimeoutSeconds) })
                    repoJson = client.GetStringAsync(src.VpmRepoUrl).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                throw new McpToolException($"failed to fetch VPM repo {src.VpmRepoUrl}: {e.Message}");
            }

            JObject repo;
            try { repo = JObject.Parse(repoJson); }
            catch { throw new McpToolException($"VPM repo {src.VpmRepoUrl} is not valid JSON"); }

            var pkg = repo["packages"]?[src.PackageId];
            if (pkg == null)
                throw new McpToolException($"package {src.PackageId} not found in VPM repo {src.VpmRepoUrl}");

            var versions = pkg["versions"] as JObject;
            if (versions == null)
                throw new McpToolException($"package {src.PackageId} has no versions in repo");

            string latest = pkg["dist-tags"]?["latest"]?.ToString();
            if (string.IsNullOrEmpty(latest) || versions[latest] == null)
            {
                latest = versions.Properties()
                    .Select(p => p.Name)
                    .OrderByDescending(v => ParseVersion(v))
                    .FirstOrDefault();
            }
            if (latest == null)
                throw new McpToolException($"cannot determine latest version for {src.PackageId}");

            var url = versions[latest]?["url"]?.ToString();
            if (string.IsNullOrEmpty(url))
                throw new McpToolException($"package {src.PackageId} v{latest} has no download URL");
            return url;
        }

        /// <summary>Download a URL to a file; on failure deletes the partial file and throws.</summary>
        private static void DownloadToFile(string url, string path, out string error)
        {
            error = null;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(kDownloadTimeoutSeconds) })
                using (var resp = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    if (resp.Content.Headers.ContentLength > kMaxDownloadBytes)
                        throw new McpToolException($"download exceeds size budget ({kMaxDownloadBytes} bytes)");
                    using (var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                        stream.CopyTo(fs);
                }
                if (new FileInfo(path).Length > kMaxDownloadBytes)
                {
                    throw new McpToolException($"download exceeds size budget ({kMaxDownloadBytes} bytes)");
                }
            }
            catch (Exception e)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                error = $"download failed: {e.Message}";
                throw new McpToolException(error);
            }
        }

        /// <summary>Read package.json from the zip ROOT without extracting; validate it parses.</summary>
        private static bool TryReadZipPackageJsonName(string zipPath, out string name, out string error)
        {
            name = null;
            error = null;
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var entry = zip.Entries.FirstOrDefault(e =>
                        e.FullName == "package.json" || e.FullName.EndsWith("/package.json", StringComparison.Ordinal));
                    if (entry == null)
                    {
                        error = "zip contains no package.json at its root";
                        return false;
                    }
                    string text;
                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                        text = reader.ReadToEnd();
                    var json = JObject.Parse(text);
                    name = json["name"]?.ToString();
                    if (string.IsNullOrEmpty(name))
                    {
                        error = "package.json has no name field";
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception e)
            {
                error = $"failed to read package.json from zip: {e.Message}";
                return false;
            }
        }

        /// <summary>Remove the installed package directory for a family (idempotent).</summary>
        public static bool TryRemove(string family, out string error)
        {
            error = null;
            if (family == null || !kShaderInstallSources.TryGetValue(family, out var src))
            {
                error = $"family '{family}' is not in the install whitelist";
                return false;
            }
            var pkgDir = Path.Combine(ChannelFile.ProjectDir, "Packages", src.PackageId);
            try
            {
                if (Directory.Exists(pkgDir))
                {
                    Directory.Delete(pkgDir, recursive: true);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }
                return true;
            }
            catch (Exception e)
            {
                error = $"failed to remove {pkgDir}: {e.Message}";
                return false;
            }
        }

        private static string ParseVersionFromUrl(string url)
        {
            var file = Path.GetFileName(new Uri(url).LocalPath);
            // jp.lilxyzw.liltoon-2.3.4.zip
            var dash = file.IndexOf('-');
            if (dash >= 0)
            {
                var v = file.Substring(dash + 1);
                if (v.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    v = v.Substring(0, v.Length - 4);
                return v;
            }
            return "?";
        }

        private static Version ParseVersion(string s)
        {
            Version.TryParse(s, out var v);
            return v ?? new Version(0, 0, 0);
        }

        [McpTool("apply_shader_package_install",
            "Install (or remove) a whitelisted third-party shader package (currently: lilToon). " +
            "Install resolves the latest version from the whitelisted VPM repo, validates the " +
            "zip's package.json name, unpacks into Packages/ (zip-slip guarded) and refreshes. " +
            "Params: {\"family\":\"lilToon\"} - optional \"localZipPath\" installs from a local zip " +
            "(offline/no network), optional \"remove\":true uninstalls. Not whitelisted families are " +
            "rejected. Approval: this tool mutates the project's Packages/ - apply_* permission applies.")]
        public static string ApplyShaderPackageInstall(string argsJson, McpToolContext ctx)
        {
            var a = JsonUtility.FromJson<ApplyInstallArgs>(argsJson ?? "{}");
            var r = new JObject();
            r["family"] = a.family;

            if (a.remove)
            {
                string err;
                var removed = TryRemove(a.family, out err);
                r["action"] = removed ? "removed" : "error";
                if (!removed) r["error"] = err;
                return r.ToString(Newtonsoft.Json.Formatting.None);
            }

            string version, source, error;
            var status = TryAutoInstall(a.family, a.localZipPath, out version, out source, out error);
            r["source"] = source;
            switch (status)
            {
                case InstallStatus.NotInWhitelist:
                    r["action"] = "rejected";
                    r["error"] = $"family '{a.family}' is not in the install whitelist (lilToon only)";
                    break;
                case InstallStatus.AlreadyInstalled:
                    r["action"] = "alreadyInstalled";
                    r["version"] = version;
                    break;
                case InstallStatus.Installed:
                    r["action"] = "installed";
                    r["version"] = version;
                    break;
                default:
                    r["action"] = "error";
                    r["error"] = error;
                    break;
            }
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [Serializable]
        private class ApplyInstallArgs
        {
            public string family;
            public string localZipPath;
            public bool remove;
        }
    }
}
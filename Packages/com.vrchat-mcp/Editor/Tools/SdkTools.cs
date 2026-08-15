using System;
using System.IO;
using UnityEditor;
using Newtonsoft.Json.Linq;
using VrcMcp.Core;

namespace VrcMcp.Tools
{
    /// <summary>
    /// VRChat SDK integration tools. SDK access is reflection-based (no hard
    /// reference to VRCSDK3A/VRCSDKBase) so the bridge keeps compiling when the
    /// SDK is absent; presence is reported via CoreTools.VrcSdkDetector.
    /// </summary>
    public static class SdkTools
    {
        /// <summary>Control panel menu entry shipped by com.vrchat.base.</summary>
        public const string ControlPanelMenuItem = "VRChat SDK/Show Control Panel";

        private static Type _avatarDescriptorType;

        /// <summary>
        /// VRCAvatarDescriptor type from the VRCSDK3A precompiled assembly
        /// (null if the SDK is absent or the type name drifted across SDK versions).
        /// </summary>
        public static Type AvatarDescriptorType
        {
            get
            {
                if (_avatarDescriptorType == null)
                {
                    try
                    {
                        _avatarDescriptorType = Type.GetType(
                            "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRCSDK3A");
                    }
                    catch { }
                }
                return _avatarDescriptorType;
            }
        }

        /// <summary>Whether the SDK is present (reflection resolution of the descriptor type).</summary>
        public static bool SdkResolvable => AvatarDescriptorType != null;

        /// <summary>Open the VRChat SDK Build Control Panel via its menu item.</summary>
        public static bool OpenControlPanel()
        {
            return EditorApplication.ExecuteMenuItem(ControlPanelMenuItem);
        }

        [McpTool("open_vrc_control_panel",
            "Open the VRChat SDK Build Control Panel window (menu VRChat SDK > Show Control Panel). " +
            "Requires com.vrchat.avatars to be installed and compiled. Returns {\"opened\":bool, \"menuItem\":...}; " +
            "opened=false means the SDK menu is missing - check the Console for SDK compile errors.")]
        public static string OpenVrcControlPanel(string argsJson, McpToolContext ctx)
        {
            if (!VrcSdkDetector.IsInstalled)
                throw new McpToolException(
                    "VRCSDK (com.vrchat.avatars) is not installed - add it via the VCC or manifest first");

            var opened = OpenControlPanel();
            var r = new JObject();
            r["opened"] = opened;
            r["menuItem"] = ControlPanelMenuItem;
            if (!opened)
                r["hint"] = "menu item not found - check the Console for SDK compile errors";
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }

        [McpTool("sdk_repair_test_files",
            "Remove the two known broken official-SDK test files (VTPTests.cs / " +
            "AssetBundleFooterTest.cs) from the installed com.vrchat.base package if present. " +
            "These files break VRC.SDKBase.Editor compilation and a VCC package re-sync " +
            "restores them - re-run this after any VCC update or when " +
            "get_project_info.sdkHealth.status is 'broken'. Idempotent.")]
        public static string SdkRepairTestFiles(string argsJson, McpToolContext ctx)
        {
            if (string.IsNullOrEmpty(VrcSdkDetector.BasePackageDir))
                throw new McpToolException(
                    "cannot locate com.vrchat.base package directory (expected file: or embedded install)");

            var repaired = new JArray();
            var alreadyClean = new JArray();
            foreach (var rel in VrcSdkDetector.KnownBrokenTestFiles)
            {
                var path = Path.Combine(VrcSdkDetector.BasePackageDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var meta = path + ".meta";
                bool existed = File.Exists(path) || File.Exists(meta);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(meta)) File.Delete(meta);
                if (existed) repaired.Add(rel); else alreadyClean.Add(rel);
            }
            var r = new JObject();
            r["baseDir"] = VrcSdkDetector.BasePackageDir;
            r["repaired"] = repaired;
            r["alreadyClean"] = alreadyClean;
            return r.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

public static class DumpApi
{
    public static void Dump()
    {
        var sb = new StringBuilder();
        string[] types = {
            "UnityEditor.MPE.ChannelService",
            "UnityEditor.MPE.ChannelClient",
            "UnityEditor.MPE.ChannelInfo",
            "UnityEditor.MPE.ChannelClientInfo",
            "UnityEditor.LogEntries",
            "UnityEditor.LogEntry"
        };
        foreach (var tn in types)
        {
            var t = Type.GetType(tn + ", UnityEditor");
            sb.AppendLine("== " + tn + " -> " + (t != null ? "FOUND" : "MISSING"));
            if (t == null) continue;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMethods(flags).OrderBy(m => m.Name))
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name));
                sb.AppendLine("  M " + (m.IsStatic ? "static " : "") + m.ReturnType.FullName + " " + m.Name + "(" + ps + ")");
            }
            foreach (var f in t.GetFields(flags))
                sb.AppendLine("  F " + f.FieldType.FullName + " " + f.Name);
        }
        var outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "api-dump2.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log("[DumpApi] wrote " + outPath);
    }
}
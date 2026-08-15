using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class PropDump
{
    [MenuItem("Tools/PropDump/Run")]
    public static void Dump()
    {
        var go = new GameObject("tmp");
        var al = go.AddComponent<AudioListener>();
        var so = new SerializedObject(al);
        var it = so.GetIterator();
        var names = new List<string>();
        while (it.NextVisible(true)) names.Add(it.propertyPath);
        Debug.Log($"[PropDump] AudioListener visible: {string.Join(",", names)}");
        var it2 = so.GetIterator();
        var names2 = new List<string>();
        while (it2.Next(true)) names2.Add(it2.propertyPath);
        Debug.Log($"[PropDump] AudioListener raw: {string.Join(",", names2)}");
        Object.DestroyImmediate(go);
    }
}

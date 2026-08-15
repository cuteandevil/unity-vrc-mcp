using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VrcMcp.Spike
{
    /// <summary>
    /// Spike: does an AnimatorController created inside a batch survive a single
    /// PerformUndo? AnimatorController is an ASSET (not scene state) - the generic
    /// batch-integrity regression only snapshots the scene tree, so this must be
    /// probed before designing create_animator_controller/add_transition.
    /// Run: Unity -batchmode -nographics -quit -executeMethod VrcMcp.Spike.SpikeAnimatorUndo.Run
    /// Writes spike_out.txt into the verify folder.
    /// </summary>
    public static class SpikeAnimatorUndo
    {
        private const string Path_ = "Assets/SpikeAC.controller";
        private const string Out = "spike_out.txt";

        public static void Run()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(Path_) != null)
                    AssetDatabase.DeleteAsset(Path_);
                AssetDatabase.SaveAssets();

                // ---- probe 1: plain CreateAnimatorControllerAtPath, NO undo registration ----
                var ac = AnimatorController.CreateAnimatorControllerAtPath(Path_);
                var sm = ac.layers[0].stateMachine;
                var st = sm.AddState("Idle");
                var clip = new AnimationClip();
                AssetDatabase.AddObjectToAsset(clip, ac);
                st.motion = clip;
                AssetDatabase.SaveAssets();
                bool p1File = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(Path_) != null;
                sb.AppendLine("probe1_create_file_exists=" + p1File);

                Undo.PerformUndo();
                bool p1FileAfter = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(Path_) != null;
                bool p1ObjAlive = ac != null;
                sb.AppendLine("probe1_undo_file_exists=" + p1FileAfter);
                sb.AppendLine("probe1_undo_obj_alive=" + p1ObjAlive);
                sb.AppendLine("probe1_verdict=" + (p1FileAfter ? "FILE_SURVIVES" : "FILE_GONE"));

                // ---- probe 2: with Undo.RegisterCreatedObjectUndo on the controller ----
                AssetDatabase.DeleteAsset(Path_);
                AssetDatabase.SaveAssets();
                int group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("spike ac");
                var ac2 = AnimatorController.CreateAnimatorControllerAtPath(Path_);
                Undo.RegisterCreatedObjectUndo(ac2, "spike ac create");
                var sm2 = ac2.layers[0].stateMachine;
                var st2 = sm2.AddState("Idle");
                var clip2 = new AnimationClip();
                AssetDatabase.AddObjectToAsset(clip2, ac2);
                st2.motion = clip2;
                Undo.CollapseUndoOperations(group);
                AssetDatabase.SaveAssets();
                bool p2File = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(Path_) != null;
                sb.AppendLine("probe2_create_file_exists=" + p2File);

                Undo.PerformUndo();
                bool p2FileAfter = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(Path_) != null;
                bool p2ObjAlive = ac2 != null;
                sb.AppendLine("probe2_undo_file_exists=" + p2FileAfter);
                sb.AppendLine("probe2_undo_obj_alive=" + p2ObjAlive);
                sb.AppendLine("probe2_verdict=" + (p2FileAfter ? "FILE_SURVIVES" : "FILE_GONE"));

                // ---- probe 3: asset delete rollback via RegisterCompleteObjectUndo BEFORE delete ----
                // (previous version destroyed the object first - that was a script bug)
                var ac3 = AnimatorController.CreateAnimatorControllerAtPath(Path_);
                AssetDatabase.SaveAssets();
                int group3 = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("spike ac delete");
                Undo.RegisterCompleteObjectUndo(ac3, "spike ac delete");
                AssetDatabase.DeleteAsset(Path_);
                Undo.CollapseUndoOperations(group3);
                bool p3Deleted = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(Path_) == null;
                sb.AppendLine("probe3_deleted=" + p3Deleted);

                Undo.PerformUndo();
                bool p3FileAfter = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(Path_) != null;
                bool p3ObjAlive = ac3 != null;
                sb.AppendLine("probe3_undo_file_exists=" + p3FileAfter);
                sb.AppendLine("probe3_undo_obj_alive=" + p3ObjAlive);
                sb.AppendLine("probe3_verdict=" + (p3FileAfter ? "FILE_RESTORED" : "FILE_NOT_RESTORED"));
            }
            catch (System.Exception e)
            {
                sb.AppendLine("EXCEPTION=" + e);
            }
            File.WriteAllText(Out, sb.ToString());
            Debug.Log("[spike]\n" + sb);
            EditorApplication.Exit(0);
        }
    }
}
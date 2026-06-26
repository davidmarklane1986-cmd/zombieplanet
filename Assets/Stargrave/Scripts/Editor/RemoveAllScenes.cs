using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;

public class RemoveAllScenes : EditorWindow
{
    [MenuItem("Tools/Remove All Scenes Except SampleScene")]
    public static void ShowWindow()
    {
        GetWindow<RemoveAllScenes>("Remove Scenes");
    }

    void OnGUI()
    {
        GUILayout.Label("Remove All Scenes", EditorStyles.boldLabel);
        GUILayout.Space(10);
        
        GUILayout.Label("This will delete ALL scene files except:", EditorStyles.wordWrappedLabel);
        GUILayout.Label("• Assets/Scenes/SampleScene.unity", EditorStyles.wordWrappedLabel);
        
        GUILayout.Space(10);
        GUILayout.Label("Scenes to be deleted:", EditorStyles.boldLabel);
        
        string[] allScenes = AssetDatabase.FindAssets("t:Scene")
            .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
            .Where(path => !path.Contains("SampleScene.unity"))
            .ToArray();
        
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        foreach (string scene in allScenes)
        {
            EditorGUILayout.LabelField(scene, EditorStyles.wordWrappedLabel);
        }
        EditorGUILayout.EndScrollView();
        
        GUILayout.Space(10);
        EditorGUILayout.HelpBox($"WARNING: This will permanently delete {allScenes.Length} scene file(s)!", MessageType.Warning);
        EditorGUILayout.HelpBox("Make sure you have saved your work and have a backup!", MessageType.Warning);
        
        GUILayout.Space(10);
        
        if (GUILayout.Button($"Delete {allScenes.Length} Scene(s)", GUILayout.Height(40)))
        {
            if (EditorUtility.DisplayDialog("Delete Scenes", 
                $"Are you sure you want to permanently delete {allScenes.Length} scene file(s)?\n\nThis cannot be undone!", 
                "Yes, Delete Them", "Cancel"))
            {
                DeleteScenes(allScenes);
            }
        }
    }

    private Vector2 scrollPosition;

    static void DeleteScenes(string[] scenePaths)
    {
        int deletedCount = 0;
        int failedCount = 0;

        foreach (string scenePath in scenePaths)
        {
            try
            {
                // Delete the scene file
                if (File.Exists(scenePath))
                {
                    AssetDatabase.DeleteAsset(scenePath);
                    deletedCount++;
                    Debug.Log($"Deleted: {scenePath}");
                }
                
                // Also try to delete the .meta file
                string metaPath = scenePath + ".meta";
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                }
            }
            catch (System.Exception e)
            {
                failedCount++;
                Debug.LogError($"Failed to delete {scenePath}: {e.Message}");
            }
        }

        AssetDatabase.Refresh();

        if (deletedCount > 0)
        {
            Debug.Log($"Successfully deleted {deletedCount} scene file(s).");
        }
        if (failedCount > 0)
        {
            Debug.LogWarning($"Failed to delete {failedCount} scene file(s).");
        }

        EditorUtility.DisplayDialog("Delete Complete", 
            $"Deleted: {deletedCount} scene(s)\nFailed: {failedCount} scene(s)", 
            "OK");
    }
}

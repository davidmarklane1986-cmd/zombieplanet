using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public class CleanScene : EditorWindow
{
    [MenuItem("Tools/Clean Scene - Keep Only Planet & Player")]
    public static void ShowWindow()
    {
        GetWindow<CleanScene>("Clean Scene");
    }

    void OnGUI()
    {
        GUILayout.Label("Scene Cleaner", EditorStyles.boldLabel);
        GUILayout.Space(10);
        
        GUILayout.Label("This will remove all GameObjects except:", EditorStyles.wordWrappedLabel);
        GUILayout.Label("• Planet (and children)", EditorStyles.wordWrappedLabel);
        GUILayout.Label("• Player (and children)", EditorStyles.wordWrappedLabel);
        GUILayout.Label("• CM_Player (Cinemachine camera)", EditorStyles.wordWrappedLabel);
        GUILayout.Label("• Main Camera (if exists)", EditorStyles.wordWrappedLabel);
        GUILayout.Label("• EventSystem (for input)", EditorStyles.wordWrappedLabel);
        GUILayout.Label("• Directional Light (basic lighting)", EditorStyles.wordWrappedLabel);
        
        GUILayout.Space(20);
        
        EditorGUILayout.HelpBox("WARNING: This action cannot be undone! Make sure you have saved your scene first.", MessageType.Warning);
        
        GUILayout.Space(10);
        
        if (GUILayout.Button("Clean Scene Now", GUILayout.Height(40)))
        {
            if (EditorUtility.DisplayDialog("Clean Scene", 
                "Are you sure you want to remove all GameObjects except Planet, Player, and essential controls?\n\nThis cannot be undone!", 
                "Yes, Clean It", "Cancel"))
            {
                CleanSceneNow();
            }
        }
    }

    static void CleanSceneNow()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.isLoaded)
        {
            Debug.LogError("No active scene!");
            return;
        }

        GameObject[] allObjects = FindObjectsOfType<GameObject>();
        int deletedCount = 0;
        int keptCount = 0;

        // Objects to keep (by name or tag)
        string[] keepNames = { "Planet", "Player", "CM_Player", "Main Camera", "EventSystem", "Directional Light" };
        string[] keepTags = { "Planet", "Player" };

        foreach (GameObject obj in allObjects)
        {
            bool shouldKeep = false;

            // Check if it's a child of something we want to keep
            Transform parent = obj.transform.parent;
            while (parent != null)
            {
                if (ShouldKeepObject(parent.gameObject, keepNames, keepTags))
                {
                    shouldKeep = true;
                    break;
                }
                parent = parent.parent;
            }

            // Check the object itself
            if (!shouldKeep)
            {
                shouldKeep = ShouldKeepObject(obj, keepNames, keepTags);
            }

            if (shouldKeep)
            {
                keptCount++;
                Debug.Log($"Keeping: {GetFullPath(obj.transform)}");
            }
            else
            {
                deletedCount++;
                Debug.Log($"Deleting: {GetFullPath(obj.transform)}");
                DestroyImmediate(obj);
            }
        }

        // Mark scene as dirty
        EditorSceneManager.MarkSceneDirty(scene);

        Debug.Log($"Scene cleaned! Kept {keptCount} objects, deleted {deletedCount} objects.");
        EditorUtility.DisplayDialog("Scene Cleaned", 
            $"Scene cleaned successfully!\n\nKept: {keptCount} objects\nDeleted: {deletedCount} objects", 
            "OK");
    }

    static bool ShouldKeepObject(GameObject obj, string[] keepNames, string[] keepTags)
    {
        // Check by name
        foreach (string name in keepNames)
        {
            if (obj.name == name || obj.name.StartsWith(name))
            {
                return true;
            }
        }

        // Check by tag
        foreach (string tag in keepTags)
        {
            if (obj.CompareTag(tag))
            {
                return true;
            }
        }

        return false;
    }

    static string GetFullPath(Transform transform)
    {
        string path = transform.name;
        while (transform.parent != null)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }
        return path;
    }
}

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

public class FindMissingScripts : EditorWindow
{
    private List<string> missingScripts = new List<string>();
    private Vector2 scrollPosition;

    [MenuItem("Tools/Find Missing Scripts")]
    public static void ShowWindow()
    {
        GetWindow<FindMissingScripts>("Find Missing Scripts");
    }

    void OnGUI()
    {
        GUILayout.Label("Missing Script References", EditorStyles.boldLabel);
        GUILayout.Space(10);

        if (GUILayout.Button("Scan All Scenes and Prefabs", GUILayout.Height(30)))
        {
            ScanForMissingScripts();
        }

        GUILayout.Space(10);
        
        if (GUILayout.Button("Remove Missing Scripts from Selected", GUILayout.Height(30)))
        {
            RemoveMissingScriptsFromSelection();
        }

        GUILayout.Space(10);

        if (missingScripts.Count > 0)
        {
            GUILayout.Label($"Found {missingScripts.Count} GameObject(s) with missing scripts:", EditorStyles.boldLabel);
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            
            foreach (string item in missingScripts)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(item);
                if (GUILayout.Button("Select", GUILayout.Width(60)))
                {
                    // Try to find and select the object
                    GameObject obj = GameObject.Find(item.Split('(')[0].Trim());
                    if (obj == null)
                    {
                        // Try loading from asset path if it's a prefab
                        string[] guids = AssetDatabase.FindAssets(item.Split('(')[0].Trim() + " t:GameObject");
                        if (guids.Length > 0)
                        {
                            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                            obj = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        }
                    }
                    if (obj != null)
                    {
                        Selection.activeGameObject = obj;
                        EditorGUIUtility.PingObject(obj);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            
            EditorGUILayout.EndScrollView();

            GUILayout.Space(10);
            if (GUILayout.Button("Clear List"))
            {
                missingScripts.Clear();
            }
        }
        else
        {
            GUILayout.Label("No missing scripts found. Click 'Scan' to check.", EditorStyles.helpBox);
        }
    }

    void ScanForMissingScripts()
    {
        missingScripts.Clear();
        
        // Scan all GameObjects in scenes
        string[] sceneGuids = AssetDatabase.FindAssets("t:Scene");
        foreach (string guid in sceneGuids)
        {
            string scenePath = AssetDatabase.GUIDToAssetPath(guid);
            UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
            if (!scene.isLoaded)
            {
                scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, UnityEditor.SceneManagement.OpenSceneMode.Additive);
            }
            
            GameObject[] rootObjects = scene.GetRootGameObjects();
            foreach (GameObject obj in rootObjects)
            {
                CheckGameObject(obj, scenePath);
            }
        }

        // Scan all prefabs
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        foreach (string guid in prefabGuids)
        {
            string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab != null)
            {
                CheckGameObjectRecursive(prefab, prefabPath);
            }
        }

        Debug.Log($"Scan complete. Found {missingScripts.Count} GameObject(s) with missing scripts.");
    }

    void CheckGameObjectRecursive(GameObject obj, string assetPath)
    {
        CheckGameObject(obj, assetPath);
        foreach (Transform child in obj.transform)
        {
            CheckGameObjectRecursive(child.gameObject, assetPath);
        }
    }

    void CheckGameObject(GameObject obj, string sourcePath)
    {
        Component[] components = obj.GetComponents<Component>();
        foreach (Component comp in components)
        {
            if (comp == null)
            {
                string pathInfo = sourcePath.Contains("Assets/") ? $" (Prefab: {sourcePath})" : $" (Scene: {sourcePath})";
                missingScripts.Add($"{obj.name} in {obj.transform.GetPath()}{pathInfo}");
                break; // Only report once per GameObject
            }
        }
    }

    void RemoveMissingScriptsFromSelection()
    {
        GameObject[] selection = Selection.gameObjects;
        if (selection.Length == 0)
        {
            EditorUtility.DisplayDialog("No Selection", "Please select GameObjects with missing scripts first.", "OK");
            return;
        }

        int removedCount = 0;
        foreach (GameObject obj in selection)
        {
            int count = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(obj);
            if (count > 0)
            {
                removedCount += count;
                Debug.Log($"Removed {count} missing script(s) from {obj.name}");
            }
        }

        if (removedCount > 0)
        {
            EditorUtility.DisplayDialog("Success", $"Removed {removedCount} missing script reference(s).", "OK");
            missingScripts.Clear(); // Refresh the list
        }
        else
        {
            EditorUtility.DisplayDialog("No Missing Scripts", "No missing scripts found in selected objects.", "OK");
        }
    }
}

// Extension method to get full path
public static class TransformExtensions
{
    public static string GetPath(this Transform transform)
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

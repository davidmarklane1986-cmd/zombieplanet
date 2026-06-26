using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class FixMissingURPComponents : EditorWindow
{
    [MenuItem("Tools/Fix Missing URP Components")]
    public static void ShowWindow()
    {
        GetWindow<FixMissingURPComponents>("Fix Missing URP Components");
    }

    void OnGUI()
    {
        GUILayout.Label("URP Component Fixer", EditorStyles.boldLabel);
        GUILayout.Space(10);
        
        GUILayout.Label("This tool helps fix missing URP component references.", EditorStyles.helpBox);
        GUILayout.Space(10);

        if (GUILayout.Button("Find and Fix Missing URP Components in Scene", GUILayout.Height(30)))
        {
            FixMissingComponents();
        }

        GUILayout.Space(10);
        GUILayout.Label("Instructions:", EditorStyles.boldLabel);
        GUILayout.Label("1. Select GameObjects with missing scripts in the Hierarchy", EditorStyles.wordWrappedLabel);
        GUILayout.Label("2. Click the button above to attempt automatic fix", EditorStyles.wordWrappedLabel);
        GUILayout.Label("3. If automatic fix fails, manually remove the missing script component and re-add the correct URP component", EditorStyles.wordWrappedLabel);
    }

    static void FixMissingComponents()
    {
        int fixedCount = 0;
        GameObject[] allObjects = FindObjectsOfType<GameObject>();

        foreach (GameObject obj in allObjects)
        {
            Component[] components = obj.GetComponents<Component>();
            bool hasMissing = false;

            foreach (Component comp in components)
            {
                if (comp == null)
                {
                    hasMissing = true;
                    break;
                }
            }

            if (hasMissing)
            {
                // Try to identify what component should be there based on other components
                if (obj.GetComponent<Camera>() != null)
                {
                    // Missing URP Camera component
                    UniversalAdditionalCameraData urpCam = obj.GetComponent<UniversalAdditionalCameraData>();
                    if (urpCam == null)
                    {
                        obj.AddComponent<UniversalAdditionalCameraData>();
                        fixedCount++;
                        Debug.Log($"Added URP Camera component to {obj.name}");
                    }
                }
                else if (obj.GetComponent<Light>() != null)
                {
                    // Missing URP Light component
                    UniversalAdditionalLightData urpLight = obj.GetComponent<UniversalAdditionalLightData>();
                    if (urpLight == null)
                    {
                        obj.AddComponent<UniversalAdditionalLightData>();
                        fixedCount++;
                        Debug.Log($"Added URP Light component to {obj.name}");
                    }
                }
                else if (obj.GetComponent<Volume>() != null)
                {
                    // Volume component should already be there, but check
                    Volume volume = obj.GetComponent<Volume>();
                    if (volume != null)
                    {
                        // Volume exists, missing script might be something else
                        Debug.LogWarning($"GameObject {obj.name} has Volume but also missing script - may need manual fix");
                    }
                }

                // Remove the missing script reference
                SerializedObject so = new SerializedObject(obj);
                SerializedProperty prop = so.FindProperty("m_Component");
                if (prop != null && prop.isArray)
                {
                    for (int i = prop.arraySize - 1; i >= 0; i--)
                    {
                        SerializedProperty element = prop.GetArrayElementAtIndex(i);
                        SerializedProperty component = element.FindPropertyRelative("component");
                        if (component != null && component.objectReferenceValue == null)
                        {
                            prop.DeleteArrayElementAtIndex(i);
                            so.ApplyModifiedProperties();
                            Debug.Log($"Removed missing script reference from {obj.name}");
                        }
                    }
                }
            }
        }

        if (fixedCount > 0)
        {
            Debug.Log($"Fixed {fixedCount} missing URP component(s). Please check the scene.");
        }
        else
        {
            Debug.Log("No missing URP components found or fixed. You may need to manually remove missing script components.");
        }
    }
}

using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

/// <summary>
/// Menu: Tools > Stargrave > Setup Zombie — creates Zombie Animator Controller and Zombie prefab.
/// Tools > Stargrave > Add ZombieSpawner to Scene — adds a spawner and assigns the prefab.
/// </summary>
public static class ZombieSetup
{
    const string ControllerPath = "Assets/Stargrave/Animation/ZombieAnimator.controller";
    const string PrefabPath = "Assets/Stargrave/Prefabs/Zombie.prefab";
    const string ModelPath = "Assets/GAMWILL Character Pack Monster  Bionic Cartoon Zombie Gorilla/Prefab/HUGO_T_Pose.prefab";

    [MenuItem("Tools/Stargrave/Setup Zombie (Animator + Prefab)")]
    public static void SetupZombie()
    {
        EnsureFolders();
        CreateController();
        CreatePrefab();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[ZombieSetup] Zombie controller and prefab created. Use Tools > Stargrave > Add ZombieSpawner to Scene.");
    }

    [MenuItem("Tools/Stargrave/Add ZombieSpawner to Scene")]
    public static void AddZombieSpawnerToScene()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogWarning("[ZombieSetup] Zombie prefab not found. Run Tools > Stargrave > Setup Zombie first.");
            return;
        }
        var go = new GameObject("ZombieSpawner");
        var spawner = go.AddComponent<ZombieSpawner>();
        spawner.zombiePrefab = prefab;
        spawner.zombieCount = 10;
        Undo.RegisterCreatedObjectUndo(go, "Add ZombieSpawner");
        Selection.activeGameObject = go;
        Debug.Log("[ZombieSetup] ZombieSpawner added to scene.");
    }

    static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Stargrave")) AssetDatabase.CreateFolder("Assets", "Stargrave");
        if (!AssetDatabase.IsValidFolder("Assets/Stargrave/Animation")) AssetDatabase.CreateFolder("Assets/Stargrave", "Animation");
        if (!AssetDatabase.IsValidFolder("Assets/Stargrave/Prefabs")) AssetDatabase.CreateFolder("Assets/Stargrave", "Prefabs");
    }

    static void CreateController()
    {
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
        {
            Debug.Log("[ZombieSetup] ZombieAnimator.controller already exists.");
            return;
        }
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        var layer = ctrl.layers[0];
        var sm = layer.stateMachine;
        var idle = sm.AddState("Idle", new Vector3(250, 0, 0));
        sm.defaultState = idle;
        var walk = sm.AddState("Walk", new Vector3(500, 0, 0));
        var toWalk = idle.AddTransition(walk);
        toWalk.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        toWalk.hasExitTime = false;
        var toIdle = walk.AddTransition(idle);
        toIdle.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
        toIdle.hasExitTime = false;
        EditorUtility.SetDirty(ctrl);
        Debug.Log("[ZombieSetup] Created " + ControllerPath);
    }

    static void CreatePrefab()
    {
        GameObject modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (modelPrefab == null)
            Debug.LogWarning("[ZombieSetup] Model not found at " + ModelPath + "; using placeholder.");

        GameObject root = new GameObject("Zombie");
        var rb = root.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        var cap = root.AddComponent<CapsuleCollider>();
        cap.direction = 1;
        cap.height = 1.8f;
        cap.radius = 0.35f;
        cap.center = new Vector3(0, 0.9f, 0);

        root.AddComponent<GravityBody>();
        root.AddComponent<CharacterAlign>();
        var ai = root.AddComponent<ZombieAI>();

        if (modelPrefab != null)
        {
            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab);
            model.name = "CharacterModel";
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;
            foreach (var c in model.GetComponentsInChildren<Collider>(true))
                c.enabled = false;
            var anim = model.GetComponent<Animator>();
            if (anim != null)
            {
                var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
                if (ctrl != null) anim.runtimeAnimatorController = ctrl;
                anim.applyRootMotion = false;
                ai.animator = anim;
            }
        }
        else
        {
            var placeholder = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            placeholder.name = "Placeholder";
            placeholder.transform.SetParent(root.transform, false);
            placeholder.transform.localPosition = new Vector3(0, 0.9f, 0);
            placeholder.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
            Object.DestroyImmediate(placeholder.GetComponent<CapsuleCollider>());
        }

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        Debug.Log("[ZombieSetup] Created " + PrefabPath);
    }
}

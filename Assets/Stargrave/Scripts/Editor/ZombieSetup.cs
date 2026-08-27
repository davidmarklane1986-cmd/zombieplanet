using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds zombie Animator controllers + gameplay prefabs, including Kenny character variants
/// (skins + idle/run clips) and GraveyardKit meshes. Wires weighted variants onto the scene spawner.
/// </summary>
public static class ZombieSetup
{
    const string ControllerPath = "Assets/Stargrave/Animation/ZombieAnimator.controller";
    const string PrefabPath = "Assets/Stargrave/Prefabs/Zombie.prefab";
    const string PrefabFolder = "Assets/Stargrave/Prefabs/Zombies";
    const string AnimFolder = "Assets/Stargrave/Animation/Zombies";
    const string MatFolder = "Assets/Stargrave/Materials/Zombies";
    const string HugoModelPath = "Assets/GAMWILL Character Pack Monster  Bionic Cartoon Zombie Gorilla/Prefab/HUGO_T_Pose.prefab";
    const string HugoIdleFbx = "Assets/GAMWILL Character Pack Monster  Bionic Cartoon Zombie Gorilla/Animation/Idle_1.fbx";
    const string HugoRunFbx = "Assets/GAMWILL Character Pack Monster  Bionic Cartoon Zombie Gorilla/Animation/Run.fbx";
    const string FarmerModelPath = "Assets/GAMWILL/Zombie Shooter Series Farmer Cowboy/Prefab/T_Pose.prefab";
    const string FarmerControllerPath = "Assets/GAMWILL/Zombie Shooter Series Farmer Cowboy/Animation/New Animator Controller.controller";
    const string TriggerPath = "Assets/Stargrave/.setup_kenny_zombies";

    struct KennyAnimatedDef
    {
        public string id;
        public string displayName;
        public string modelPath;
        public string idlePath;
        public string runPath;
        public string skinPath;
        public float moveSpeed;
        public float scale;
        public float weight;
        public float capsuleHeight, capsuleRadius;
    }

    struct KennyStaticDef
    {
        public string id;
        public string displayName;
        public string modelPath;
        public float moveSpeed;
        public float scale;
        public float weight;
        public float capsuleHeight, capsuleRadius;
    }

    /// <summary>
    /// Combat budget from speed: faster → fewer shots-to-kill and weaker hits.
    /// Baseline at 3 m/s ≈ Walker (≈4 HP, 10 damage). Very slow types get a tank bias.
    /// </summary>
    public static void BalanceFromSpeed(float moveSpeed, out int minHp, out int maxHp, out int damage)
    {
        const float refSpeed = 3f;
        float speed = Mathf.Max(1.5f, moveSpeed);
        float f = refSpeed / speed;

        float hpAvg = Mathf.Clamp(4f * f, 2f, 11f);
        damage = Mathf.Clamp(Mathf.RoundToInt(10f * f), 6, 22);

        // Slow tanks: extra bulk and punch so Brute-class stays threatening.
        if (speed <= 2.4f)
        {
            hpAvg = Mathf.Min(12f, hpAvg * 1.6f);
            damage = Mathf.Min(24, Mathf.RoundToInt(damage * 1.25f));
        }

        minHp = Mathf.Max(2, Mathf.RoundToInt(hpAvg * 0.85f));
        maxHp = Mathf.Max(minHp, Mathf.RoundToInt(hpAvg * 1.25f));
    }

    static readonly KennyAnimatedDef[] AnimatedKenny =
    {
        new KennyAnimatedDef
        {
            id = "Walker", displayName = "Zombie Walker",
            modelPath = StargravePlayableHumanoidImport.KennySurvivorsHumanoid,
            idlePath = "", runPath = "",
            skinPath = "Assets/ThirdParty/Kenny/Charecters/Survivors/Skins/zombieA.png",
            moveSpeed = 3.0f, scale = 1.45f, weight = 48f,
            capsuleHeight = 1.85f, capsuleRadius = 0.32f
        },
        new KennyAnimatedDef
        {
            id = "Runner", displayName = "Zombie Runner",
            modelPath = StargravePlayableHumanoidImport.KennySurvivorsHumanoid,
            idlePath = "", runPath = "",
            skinPath = "Assets/ThirdParty/Kenny/Charecters/Survivors/Skins/zombieC.png",
            moveSpeed = 5.6f, scale = 1.35f, weight = 30f,
            capsuleHeight = 1.75f, capsuleRadius = 0.3f
        },
        new KennyAnimatedDef
        {
            id = "Brute", displayName = "Zombie Brute",
            modelPath = StargravePlayableHumanoidImport.KennyRetroHumanoid,
            idlePath = "", runPath = "",
            skinPath = "Assets/ThirdParty/Kenny/Charecters/Retro/Skins/zombieMaleA.png",
            moveSpeed = 2.1f, scale = 1.85f, weight = 16f,
            capsuleHeight = 2.1f, capsuleRadius = 0.42f
        },
        new KennyAnimatedDef
        {
            id = "Stalker", displayName = "Zombie Stalker",
            modelPath = StargravePlayableHumanoidImport.KennyRetroHumanoid,
            idlePath = "", runPath = "",
            skinPath = "Assets/ThirdParty/Kenny/Charecters/Retro/Skins/zombieFemaleA.png",
            moveSpeed = 4.2f, scale = 1.4f, weight = 16f,
            capsuleHeight = 1.8f, capsuleRadius = 0.3f
        },
    };

    static readonly KennyStaticDef[] StaticKenny =
    {
        new KennyStaticDef
        {
            id = "Graveyard", displayName = "Graveyard Zombie",
            modelPath = "Assets/ThirdParty/Kenny/GraveyardKit/Models/FBX format/character-zombie.fbx",
            moveSpeed = 2.7f, scale = 1.55f, weight = 6f,
            capsuleHeight = 1.9f, capsuleRadius = 0.35f
        },
        new KennyStaticDef
        {
            id = "Skeleton", displayName = "Skeleton",
            modelPath = "Assets/ThirdParty/Kenny/GraveyardKit/Models/FBX format/character-skeleton.fbx",
            moveSpeed = 3.9f, scale = 1.5f, weight = 6f,
            capsuleHeight = 1.85f, capsuleRadius = 0.3f
        },
        new KennyStaticDef
        {
            id = "Vampire", displayName = "Vampire",
            modelPath = "Assets/ThirdParty/Kenny/GraveyardKit/Models/FBX format/character-vampire.fbx",
            moveSpeed = 4.8f, scale = 1.5f, weight = 4f,
            capsuleHeight = 1.9f, capsuleRadius = 0.32f
        },
    };

    [InitializeOnLoadMethod]
    static void AutoRunFromTrigger()
    {
        EditorApplication.delayCall += () =>
        {
            if (!File.Exists(TriggerPath))
                return;
            try { File.Delete(TriggerPath); } catch { /* ignore */ }
            SetupKennyZombieVariants();
        };
    }

    [MenuItem("Tools/Stargrave/Setup Zombie (Animator + Prefab)")]
    public static void SetupZombie()
    {
        EnsureFolders();
        CreateLegacyController();
        CreateLegacyHugoPrefab();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[ZombieSetup] Legacy HUGO zombie ready. Prefer Tools > Stargrave > Setup Kenny Zombie Variants for multi-type.");
    }

    [MenuItem("Tools/Stargrave/Setup Kenny Zombie Variants")]
    public static void SetupKennyZombieVariants()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ZombieSetup] Unity is still compiling/importing. Wait for the spinner, then run again.");
            return;
        }

        EnsureFolders();
        StargravePlayableHumanoidImport.EnsureHumanoidPipeline();
        var built = new List<ZombieSpawnVariant>();

        // Keep existing HUGO as a rare heavy type if present / creatable.
        CreateLegacyController();
        CreateLegacyHugoPrefab();
        var hugo = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (hugo != null)
        {
            ApplyAiStats(hugo, 2.4f);
            built.Add(new ZombieSpawnVariant { name = "Hugo Brute", prefab = hugo, weight = 12f });
        }

        // Animated Kenny characters (idle/run).
        foreach (var def in AnimatedKenny)
        {
            var prefab = BuildAnimatedKennyPrefab(def);
            if (prefab != null)
                built.Add(new ZombieSpawnVariant { name = def.displayName, prefab = prefab, weight = def.weight });
        }

        // GraveyardKit static meshes (no clips in the pack — still valid enemies).
        foreach (var def in StaticKenny)
        {
            var prefab = BuildStaticKennyPrefab(def);
            if (prefab != null)
                built.Add(new ZombieSpawnVariant { name = def.displayName, prefab = prefab, weight = def.weight });
        }

        // Bonus: Farmer cowboy if present (has its own animator).
        var farmer = BuildFromExistingCharacterPrefab(
            "FarmerZombie", FarmerModelPath, 3.4f, 1.15f, 1.9f, 0.35f);
        if (farmer != null)
            built.Add(new ZombieSpawnVariant { name = "Farmer Zombie", prefab = farmer, weight = 18f });

        WireSpawner(built);
        PublishLocomotionResources();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[ZombieSetup] Built {built.Count} zombie variant(s) and wired ZombieSpawner.variants.");
    }

    static void PublishLocomotionResources()
    {
        const string resRoot = "Assets/Stargrave/Resources";
        const string resLocomotion = resRoot + "/ZombieLocomotion";
        if (!AssetDatabase.IsValidFolder("Assets/Stargrave/Resources"))
            AssetDatabase.CreateFolder("Assets/Stargrave", "Resources");
        if (!AssetDatabase.IsValidFolder(resLocomotion))
            AssetDatabase.CreateFolder(resRoot, "ZombieLocomotion");

        foreach (var def in AnimatedKenny)
        {
            string idleSrc = $"{AnimFolder}/Zombie_{def.id}_Idle_Upright.anim";
            string runSrc = $"{AnimFolder}/Zombie_{def.id}_Run_Upright.anim";
            string idleDst = $"{resLocomotion}/{def.id}_Idle.anim";
            string runDst = $"{resLocomotion}/{def.id}_Run.anim";
            CopyAnimResource(idleSrc, idleDst);
            CopyAnimResource(runSrc, runDst);
        }
    }

    static void CopyAnimResource(string src, string dst)
    {
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(src) == null)
            return;
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(dst) != null)
            AssetDatabase.DeleteAsset(dst);
        AssetDatabase.CopyAsset(src, dst);
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(dst);
        if (clip != null)
        {
            clip.legacy = false;
            clip.wrapMode = WrapMode.Loop;
            EditorUtility.SetDirty(clip);
        }
    }

    [MenuItem("Tools/Stargrave/Probe Zombie Orientation")]
    public static void ProbeZombieOrientation()
    {
        var go = GameObject.Find("_ZombieOrientProbe");
        if (go == null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabFolder}/Zombie_Walker.prefab");
            if (prefab == null)
            {
                Debug.LogWarning("[ZombieSetup] Walker prefab missing.");
                return;
            }
            go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = "_ZombieOrientProbe";
        }

        var anim = go.GetComponentInChildren<Animator>();
        if (anim != null && anim.runtimeAnimatorController != null)
        {
            anim.Play("Idle", 0, 0f);
            anim.Update(0f);
        }

        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            Debug.LogWarning("[ZombieSetup] Probe: no renderers.");
            return;
        }

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);

        Transform rootBone = go.transform.Find("CharacterModel/Root");
        Transform head = null;
        foreach (var t in go.GetComponentsInChildren<Transform>())
        {
            if (t.name == "Head") { head = t; break; }
        }

        string posture = b.size.y >= b.size.x && b.size.y >= b.size.z
            ? "STANDING" : "LAYING";

        var sb = new System.Text.StringBuilder();
        Transform modelTf = go.transform.Find("CharacterModel");
        sb.AppendLine($"CharacterModel scale={(modelTf != null ? modelTf.localScale.ToString("F4") : "?")} lossy={(modelTf != null ? modelTf.lossyScale.ToString("F4") : "?")} pos={(modelTf != null ? modelTf.localPosition.ToString("F3") : "?")}");
        sb.AppendLine($"bounds={b.size} → {posture}");
        sb.AppendLine($"Root euler={(rootBone != null ? rootBone.localEulerAngles.ToString() : "missing")}");
        sb.AppendLine($"HeadY={(head != null ? head.position.y.ToString("F3") : "?")} minY={b.min.y:F3}");

        Transform hipsCtrl = go.transform.Find("CharacterModel/Root/HipsCtrl");
        sb.AppendLine($"HipsCtrl euler={(hipsCtrl != null ? hipsCtrl.localEulerAngles.ToString() : "missing")}");

        var idle = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/ThirdParty/Kenny/Charecters/Survivors/Animations/idle.fbx");
        var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/ThirdParty/Kenny/Charecters/Survivors/Model/characterMedium.fbx");
        AppendHipsPath(sb, idle, "idle.fbx");
        AppendHipsPath(sb, modelAsset, "characterMedium.fbx");

        // Disable animator and remeasure bind pose
        if (anim != null)
        {
            anim.enabled = false;
            renderers = go.GetComponentsInChildren<Renderer>();
            b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            string bindPosture = b.size.y >= b.size.x && b.size.y >= b.size.z ? "STANDING" : "LAYING";
            sb.AppendLine($"bindPose(noAnim) bounds={b.size} → {bindPosture}");
            anim.enabled = true;
        }

        File.WriteAllText("Assets/Stargrave/.zombie_orient_probe.txt", sb.ToString());
        Debug.Log("[ZombieSetup] Probe:\n" + sb);
    }

    static void AppendHipsPath(System.Text.StringBuilder sb, GameObject asset, string label)
    {
        if (asset == null) { sb.AppendLine(label + ": missing"); return; }
        Transform root = null, hips = null;
        foreach (var t in asset.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "Root" && root == null) root = t;
            if (t.name == "HipsCtrl" && hips == null) hips = t;
        }
        string hipsPath = "?";
        if (hips != null)
        {
            var parts = new List<string>();
            for (Transform c = hips; c != null && c != asset.transform; c = c.parent)
                parts.Add(c.name);
            parts.Reverse();
            hipsPath = string.Join("/", parts);
        }
        sb.AppendLine($"{label}: Root.euler={(root != null ? root.localEulerAngles.ToString() : "none")} HipsPath={hipsPath}");
    }

    [MenuItem("Tools/Stargrave/Rebuild Kenny Upright Clips Only")]
    public static void RebuildUprightClipsOnly()
    {
        EnsureFolders();
        var log = new System.Text.StringBuilder();
        foreach (var def in AnimatedKenny)
        {
            try
            {
                var idle = GetOrCreateUprightClip(def.id, "Idle", def.idlePath);
                var run = GetOrCreateUprightClip(def.id, "Run", def.runPath);
                int idleRoot = CountRootPaths(idle);
                int runRoot = CountRootPaths(run);
                log.AppendLine($"{def.id}: idleBindings={CountBindings(idle)} idleRootPrefixed={idleRoot} runRootPrefixed={runRoot}");
                var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>($"{AnimFolder}/Zombie_{def.id}.controller");
                if (ctrl != null)
                    WireIdleRun(ctrl, def.id, def.idlePath, def.runPath);
            }
            catch (System.Exception ex)
            {
                log.AppendLine($"{def.id} FAILED: {ex}");
            }
        }
        File.WriteAllText("Assets/Stargrave/.zombie_clip_rebuild.txt", log.ToString());
        AssetDatabase.SaveAssets();
        Debug.Log("[ZombieSetup] Upright clip rebuild:\n" + log);
    }

    static int CountBindings(AnimationClip clip)
    {
        return clip == null ? 0 : AnimationUtility.GetCurveBindings(clip).Length;
    }

    static int CountRootPaths(AnimationClip clip)
    {
        if (clip == null) return 0;
        int n = 0;
        foreach (var b in AnimationUtility.GetCurveBindings(clip))
        {
            if (b.path != null && b.path.StartsWith("Root/", System.StringComparison.Ordinal))
                n++;
        }
        return n;
    }

    [MenuItem("Tools/Stargrave/Arm Kenny Zombie Variant Setup")]
    public static void ArmKennySetup()
    {
        EnsureFolders();
        File.WriteAllText(TriggerPath, "setup");
        AssetDatabase.Refresh();
        Debug.Log("[ZombieSetup] Armed. Focus Unity / wait for domain reload to build Kenny zombie variants.");
    }

    [MenuItem("Tools/Stargrave/Add ZombieSpawner to Scene")]
    public static void AddZombieSpawnerToScene()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogWarning("[ZombieSetup] Zombie prefab not found. Run Setup Kenny Zombie Variants first.");
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
        if (!AssetDatabase.IsValidFolder(PrefabFolder)) AssetDatabase.CreateFolder("Assets/Stargrave/Prefabs", "Zombies");
        if (!AssetDatabase.IsValidFolder(AnimFolder)) AssetDatabase.CreateFolder("Assets/Stargrave/Animation", "Zombies");
        if (!AssetDatabase.IsValidFolder("Assets/Stargrave/Materials")) AssetDatabase.CreateFolder("Assets/Stargrave", "Materials");
        if (!AssetDatabase.IsValidFolder(MatFolder)) AssetDatabase.CreateFolder("Assets/Stargrave/Materials", "Zombies");
    }

    static void CreateLegacyController()
    {
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
            return;
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        var sm = ctrl.layers[0].stateMachine;
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
    }

    static void CreateLegacyHugoPrefab()
    {
        // Always rebuild so FitModelHeight / CharacterAlign offsets stay correct.
        if (AssetDatabase.LoadAssetAtPath<GameObject>(HugoModelPath) == null)
            return;

        // Hugo has its own Idle_1 / Run FBXs — do not put Kenny Survivor clips on him.
        AnimatorController ctrl = GetOrCreatePackClipController("Hugo", HugoIdleFbx, HugoRunFbx);

        GameObject modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HugoModelPath);
        GameObject root = BuildGameplayRoot("Zombie", 1.8f, 0.35f, 1f);
        var ai = root.GetComponent<ZombieAI>();
        ApplyAiStatsOnComponent(ai, 2.4f);

        if (modelPrefab != null)
        {
            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab);
            model.name = "CharacterModel";
            AttachPackAnimatorLocomotion(root, model, ai, ctrl, keepNestedAnimator: true);
            FitModelHeight(model, 1.9f);
        }

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
    }

    static GameObject BuildAnimatedKennyPrefab(KennyAnimatedDef def)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(def.modelPath) == null)
        {
            Debug.LogWarning($"[ZombieSetup] Missing Humanoid model {def.modelPath}");
            return null;
        }

        EnsureReadableTexture(def.skinPath);
        Material mat = GetOrCreateSkinMaterial(def.id, def.skinPath);
        // Same farmer Humanoid retarget as playable Kenny (Idle / Walk / Death).
        AnimatorController ctrl = GetOrCreateFarmerHumanoidZombieController(def.id);

        string prefabPath = $"{PrefabFolder}/Zombie_{def.id}.prefab";
        GameObject root = BuildGameplayRoot(
            $"Zombie_{def.id}", def.capsuleHeight, def.capsuleRadius, def.scale, Vector3.zero);
        var ai = root.GetComponent<ZombieAI>();
        ApplyAiStatsOnComponent(ai, def.moveSpeed);

        GameObject model = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(def.modelPath));
        model.name = "CharacterModel";
        AttachKennyHumanoidLocomotion(root, model, ai, ctrl, def.modelPath);
        FitModelHeight(model, Mathf.Max(1.4f, def.capsuleHeight * 0.92f * (def.scale / 1.5f)));
        var scaleLock = model.GetComponent<FittedVisualScaleLock>();
        if (scaleLock != null)
            scaleLock.SetLockedScale(model.transform.localScale);
        ApplyMaterialRecursive(model, mat);
        root.GetComponent<CharacterAlign>().Align();

        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        return AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
    }

    /// <summary>Humanoid farmer Idle/Walk/Death — retargets onto Kenny Humanoid avatars.</summary>
    static AnimatorController GetOrCreateFarmerHumanoidZombieController(string id)
    {
        string path = $"{AnimFolder}/Zombie_{id}_FarmerHumanoid.controller";
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
            AssetDatabase.DeleteAsset(path);

        AnimationClip idle = StargravePlayableHumanoidImport.LoadFirstClip(StargravePlayableHumanoidImport.FarmerIdleMenu);
        AnimationClip run = StargravePlayableHumanoidImport.LoadFirstClip(StargravePlayableHumanoidImport.FarmerRunFront);
        AnimationClip death = StargravePlayableHumanoidImport.LoadFirstClip(StargravePlayableHumanoidImport.FarmerDeath);

        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        var sm = ctrl.layers[0].stateMachine;
        while (sm.states.Length > 0)
            sm.RemoveState(sm.states[0].state);

        var idleState = sm.AddState("Idle", new Vector3(250, 0, 0));
        idleState.motion = idle;
        sm.defaultState = idleState;

        var walkState = sm.AddState("Walk", new Vector3(500, 0, 0));
        walkState.motion = run != null ? run : idle;

        var deathState = sm.AddState("root|Death", new Vector3(250, 120, 0));
        deathState.motion = death != null ? death : idle;

        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    /// <summary>Player-style stack: Humanoid farmer clips + ZombieLocomotionAnimator.Play(Idle|Walk).</summary>
    static void AttachKennyHumanoidLocomotion(GameObject root, GameObject model, ZombieAI ai,
        RuntimeAnimatorController ctrl, string humanoidModelPath)
    {
        model.transform.SetParent(root.transform, false);
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Vector3.one;

        foreach (var c in model.GetComponentsInChildren<Collider>(true))
            c.enabled = false;

        foreach (var old in model.GetComponentsInChildren<Animator>(true))
            Object.DestroyImmediate(old);
        foreach (var old in model.GetComponentsInChildren<Animation>(true))
            Object.DestroyImmediate(old);
        foreach (var old in model.GetComponentsInChildren<KennyLocomotionDriver>(true))
            Object.DestroyImmediate(old);
        foreach (var old in model.GetComponentsInChildren<ZombieLocomotionAnimator>(true))
            Object.DestroyImmediate(old);

        var anim = model.AddComponent<Animator>();
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        anim.runtimeAnimatorController = ctrl;

        Avatar humanAvatar = StargravePlayableHumanoidImport.LoadEmbeddedAvatar(humanoidModelPath);
        if (humanAvatar != null && humanAvatar.isValid)
            anim.avatar = humanAvatar;
        else
            Debug.LogWarning($"[ZombieSetup] No valid Humanoid avatar for {humanoidModelPath}");

        var loco = model.AddComponent<ZombieLocomotionAnimator>();
        loco.animator = anim;
        loco.idleStateName = "Idle";
        loco.walkStateName = "Walk";

        var scaleLock = model.GetComponent<FittedVisualScaleLock>();
        if (scaleLock == null)
            scaleLock = model.AddComponent<FittedVisualScaleLock>();

        if (ai != null)
        {
            ai.locomotion = null;
            ai.locomotionIdle = null;
            ai.locomotionRun = null;
            ai.animator = anim;
        }
    }

    static GameObject BuildStaticKennyPrefab(KennyStaticDef def)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(def.modelPath) == null)
        {
            Debug.LogWarning($"[ZombieSetup] Missing model {def.modelPath}");
            return null;
        }

        string prefabPath = $"{PrefabFolder}/Zombie_{def.id}.prefab";
        GameObject root = BuildGameplayRoot($"Zombie_{def.id}", def.capsuleHeight, def.capsuleRadius, def.scale);
        var ai = root.GetComponent<ZombieAI>();
        ApplyAiStatsOnComponent(ai, def.moveSpeed);

        GameObject model = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(def.modelPath));
        model.name = "CharacterModel";
        // Rigid modular kit — no skinned clips; fake walk by swinging leg/arm parts.
        AttachProceduralLimbWalk(root, model, ai);
        FitModelHeight(model, Mathf.Max(1.4f, def.capsuleHeight * 0.92f * (def.scale / 1.5f)));

        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        return AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
    }

    static GameObject BuildFromExistingCharacterPrefab(
        string id, string sourcePrefabPath,
        float speed, float scale,
        float capsuleHeight, float capsuleRadius)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
        if (source == null)
            return null;

        string prefabPath = $"{PrefabFolder}/Zombie_{id}.prefab";
        GameObject root = BuildGameplayRoot($"Zombie_{id}", capsuleHeight, capsuleRadius, scale);
        var ai = root.GetComponent<ZombieAI>();
        ApplyAiStatsOnComponent(ai, speed);

        GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(source);
        model.name = "CharacterModel";
        // Farmer: keep pack Animator + Play Idle_Menu / Run_Front (same as player).
        var farmerCtrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(FarmerControllerPath);
        AttachPackAnimatorLocomotion(root, model, ai, farmerCtrl, keepNestedAnimator: true,
            idleState: "root|Idle_Menu", walkState: "root|Run_Front");
        FitModelHeight(model, Mathf.Max(1.4f, capsuleHeight * 0.92f * (scale / 1.15f)));

        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        return AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
    }

    static void AttachProceduralLimbWalk(GameObject root, GameObject model, ZombieAI ai)
    {
        model.transform.SetParent(root.transform, false);
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Vector3.one;

        foreach (var c in model.GetComponentsInChildren<Collider>(true))
            c.enabled = false;
        foreach (var old in model.GetComponentsInChildren<Animator>(true))
            Object.DestroyImmediate(old);
        foreach (var old in model.GetComponentsInChildren<ZombieLocomotionAnimator>(true))
            Object.DestroyImmediate(old);
        foreach (var old in model.GetComponentsInChildren<ZombieProceduralLimbWalk>(true))
            Object.DestroyImmediate(old);

        var proc = model.AddComponent<ZombieProceduralLimbWalk>();
        proc.modelRoot = model.transform;
        if (ai != null)
            ai.animator = null;
    }

    static void AttachPackAnimatorLocomotion(GameObject root, GameObject model, ZombieAI ai,
        RuntimeAnimatorController ctrl, bool keepNestedAnimator,
        string idleState = "Idle", string walkState = "Walk")
    {
        model.transform.SetParent(root.transform, false);
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Vector3.one;

        foreach (var c in model.GetComponentsInChildren<Collider>(true))
            c.enabled = false;
        foreach (var old in model.GetComponentsInChildren<KennyLocomotionDriver>(true))
            Object.DestroyImmediate(old);
        foreach (var old in model.GetComponentsInChildren<ZombieProceduralLimbWalk>(true))
            Object.DestroyImmediate(old);
        foreach (var old in model.GetComponentsInChildren<ZombieLocomotionAnimator>(true))
            Object.DestroyImmediate(old);

        Animator anim = model.GetComponentInChildren<Animator>(true);
        if (!keepNestedAnimator || anim == null)
        {
            foreach (var old in model.GetComponentsInChildren<Animator>(true))
                Object.DestroyImmediate(old);
            anim = model.AddComponent<Animator>();
        }

        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        if (ctrl != null)
            anim.runtimeAnimatorController = ctrl;

        var loco = anim.gameObject.AddComponent<ZombieLocomotionAnimator>();
        loco.animator = anim;
        loco.idleStateName = idleState;
        loco.walkStateName = walkState;
        loco.AutoPickPackStates();

        if (ai != null)
        {
            ai.animator = anim;
            ai.locomotion = null;
            ai.locomotionIdle = null;
            ai.locomotionRun = null;
        }
    }

    /// <summary>Idle/Walk controller using raw FBX clips (Hugo / pack-native bones).</summary>
    static AnimatorController GetOrCreatePackClipController(string id, string idleFbx, string runFbx)
    {
        string path = $"{AnimFolder}/Zombie_{id}.controller";
        var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        AnimationClip idleClip = FindBestClip(idleFbx);
        AnimationClip runClip = FindBestClip(runFbx);
        if (existing != null)
        {
            var smExisting = existing.layers[0].stateMachine;
            foreach (var st in smExisting.states)
            {
                if (st.state.name == "Idle" && idleClip != null)
                    st.state.motion = idleClip;
                if (st.state.name == "Walk" && runClip != null)
                    st.state.motion = runClip;
            }
            EditorUtility.SetDirty(existing);
            return existing;
        }

        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        if (System.Array.FindIndex(ctrl.parameters, p => p.name == "Speed") < 0)
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);

        var sm = ctrl.layers[0].stateMachine;
        while (sm.states.Length > 0)
            sm.RemoveState(sm.states[0].state);

        var idle = sm.AddState("Idle", new Vector3(250, 0, 0));
        sm.defaultState = idle;
        var walk = sm.AddState("Walk", new Vector3(500, 0, 0));
        idle.motion = idleClip;
        walk.motion = runClip;
        var toWalk = idle.AddTransition(walk);
        toWalk.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        toWalk.hasExitTime = false;
        toWalk.duration = 0.15f;
        var toIdle = walk.AddTransition(idle);
        toIdle.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
        toIdle.hasExitTime = false;
        toIdle.duration = 0.15f;
        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    static GameObject BuildGameplayRoot(string name, float capsuleHeight, float capsuleRadius, float scale)
    {
        return BuildGameplayRoot(name, capsuleHeight, capsuleRadius, scale, Vector3.zero);
    }

    static GameObject BuildGameplayRoot(string name, float capsuleHeight, float capsuleRadius, float scale, Vector3 modelEulerOffset)
    {
        // Root stays at uniform 1 — visual size is applied by FitModelHeight on the CharacterModel
        // (Kenny FBX often embeds a 100× scale that must be corrected on the model, not the root).
        _ = scale;
        GameObject root = new GameObject(name);
        root.transform.localScale = Vector3.one;

        var rb = root.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        var cap = root.AddComponent<CapsuleCollider>();
        cap.direction = 1;
        cap.height = capsuleHeight;
        cap.radius = capsuleRadius;
        cap.center = new Vector3(0f, capsuleHeight * 0.5f, 0f);

        root.AddComponent<GravityBody>();
        var align = root.AddComponent<CharacterAlign>();
        align.modelHeightOffset = 0f;
        align.modelEulerOffset = modelEulerOffset;
        root.AddComponent<ZombieAI>();
        root.AddComponent<ZombieVisibilityCuller>();
        return root;
    }

    /// <summary>Player-style stack: AnimatorController + ZombieLocomotionAnimator.Play(Idle|Walk).</summary>
    static void AttachKennyAnimatorLocomotion(GameObject root, GameObject model, ZombieAI ai,
        RuntimeAnimatorController ctrl)
    {
        model.transform.SetParent(root.transform, false);
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Vector3.one;

        foreach (var c in model.GetComponentsInChildren<Collider>(true))
            c.enabled = false;

        foreach (var old in model.GetComponentsInChildren<Animator>(true))
            Object.DestroyImmediate(old);
        foreach (var old in model.GetComponentsInChildren<Animation>(true))
            Object.DestroyImmediate(old);
        foreach (var old in model.GetComponentsInChildren<KennyLocomotionDriver>(true))
            Object.DestroyImmediate(old);
        foreach (var old in model.GetComponentsInChildren<ZombieLocomotionAnimator>(true))
            Object.DestroyImmediate(old);

        var anim = model.AddComponent<Animator>();
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        anim.runtimeAnimatorController = ctrl;
        try
        {
            string avatarPath = $"{AnimFolder}/Zombie_{root.name}_Avatar.asset";
            var avatar = AvatarBuilder.BuildGenericAvatar(model, "");
            if (avatar != null)
            {
                avatar.name = System.IO.Path.GetFileNameWithoutExtension(avatarPath);
                if (AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath) != null)
                    AssetDatabase.DeleteAsset(avatarPath);
                AssetDatabase.CreateAsset(avatar, avatarPath);
                anim.avatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[ZombieSetup] BuildGenericAvatar failed: {ex.Message}");
        }

        var loco = model.AddComponent<ZombieLocomotionAnimator>();
        loco.animator = anim;
        loco.idleStateName = "Idle";
        loco.walkStateName = "Walk";

        if (ai != null)
        {
            ai.locomotion = null;
            ai.locomotionIdle = null;
            ai.locomotionRun = null;
            ai.animator = anim;
        }
    }

    static void AttachModel(GameObject root, GameObject model, ZombieAI ai, RuntimeAnimatorController ctrl,
        bool keepExistingController = false, string avatarAssetPath = null)
    {
        model.transform.SetParent(root.transform, false);
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Vector3.one;

        foreach (var c in model.GetComponentsInChildren<Collider>(true))
            c.enabled = false;

        // Animator on CharacterModel (fitted scale). Clips are remapped to Root/HipsCtrl/...
        // so Generic paths bind. Do NOT put Animator on Root (×100 scale breaks playback).
        Transform animHost = model.transform;

        foreach (var old in model.GetComponentsInChildren<Animator>(true))
        {
            if (old.transform != animHost)
                Object.DestroyImmediate(old);
        }

        var anim = animHost.GetComponent<Animator>();
        if (anim == null)
            anim = animHost.gameObject.AddComponent<Animator>();
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        if (!keepExistingController && ctrl != null)
            anim.runtimeAnimatorController = ctrl;

        if (!keepExistingController && !string.IsNullOrEmpty(avatarAssetPath))
        {
            try
            {
                var avatar = AvatarBuilder.BuildGenericAvatar(animHost.gameObject, "");
                if (avatar != null)
                {
                    avatar.name = Path.GetFileNameWithoutExtension(avatarAssetPath);
                    if (AssetDatabase.LoadAssetAtPath<Avatar>(avatarAssetPath) != null)
                        AssetDatabase.DeleteAsset(avatarAssetPath);
                    AssetDatabase.CreateAsset(avatar, avatarAssetPath);
                    anim.avatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarAssetPath);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ZombieSetup] BuildGenericAvatar failed on {model.name}: {ex.Message}");
            }
        }

        if (ai != null)
            ai.animator = anim;
    }

    /// <summary>
    /// Fit visual mesh to ~human height. Kenny FBX embeds ×100 on mesh/armature;
    /// compensate on CharacterModel using measured world bounds.
    /// </summary>
    static void FitModelHeight(GameObject model, float targetHeight)
    {
        if (model == null || targetHeight <= 1e-4f)
            return;

        model.transform.localScale = Vector3.one;
        model.transform.localPosition = Vector3.zero;

        Bounds world = GetWorldVisualBounds(model);
        float height = world.size.y;
        if (height < 1e-4f)
        {
            float embedded = GetMaxEmbeddedUniformScale(model);
            model.transform.localScale = Vector3.one * (targetHeight / (1.8f * Mathf.Max(1f, embedded > 50f ? embedded : 1f)));
            return;
        }

        float s = targetHeight / height;
        model.transform.localScale = Vector3.one * s;

        world = GetWorldVisualBounds(model);
        Transform parent = model.transform.parent;
        float parentY = parent != null ? parent.TransformPoint(Vector3.zero).y : 0f;
        Vector3 lp = model.transform.localPosition;
        if (world.size.y > 0.2f && world.size.y < targetHeight * 3f)
            lp.y += parentY - world.min.y;
        else
            lp.y = 0f;
        model.transform.localPosition = lp;

        Debug.Log($"[ZombieSetup] FitModelHeight '{model.name}': worldH={height:F2} → scale={s:F5} (target={targetHeight:F2})");
    }

    static float MegaUniformScaleOf(Transform t)
    {
        if (t == null)
            return 1f;
        Vector3 ls = t.localScale;
        float m = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
        if (m < 50f)
            return 1f;
        if (Mathf.Abs(Mathf.Abs(ls.x) - m) < m * 0.15f &&
            Mathf.Abs(Mathf.Abs(ls.y) - m) < m * 0.15f &&
            Mathf.Abs(Mathf.Abs(ls.z) - m) < m * 0.15f)
            return m;
        return 1f;
    }

    static float GetMaxEmbeddedUniformScale(GameObject model)
    {
        float max = 1f;
        foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null)
                continue;
            max = Mathf.Max(max, MaxMegaScaleAlongAncestors(smr.transform, model.transform));
            if (smr.rootBone != null)
                max = Mathf.Max(max, MaxMegaScaleAlongAncestors(smr.rootBone, model.transform));
        }
        foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null)
                continue;
            max = Mathf.Max(max, MaxMegaScaleAlongAncestors(mf.transform, model.transform));
        }
        return max;
    }

    static float MaxMegaScaleAlongAncestors(Transform start, Transform stopExclusive)
    {
        float max = 1f;
        Transform t = start;
        while (t != null && t != stopExclusive)
        {
            // Ignore weapon/muzzle helpers (Farmer has a 1000× muzzle gizmo).
            string n = t.name;
            if (n.IndexOf("muzzle", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("weapon", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                Vector3 ls = t.localScale;
                float m = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                if (m >= 50f &&
                    Mathf.Abs(Mathf.Abs(ls.x) - m) < m * 0.15f &&
                    Mathf.Abs(Mathf.Abs(ls.y) - m) < m * 0.15f &&
                    Mathf.Abs(Mathf.Abs(ls.z) - m) < m * 0.15f)
                    max = Mathf.Max(max, m);
            }
            t = t.parent;
        }
        return max;
    }

    static float GetMaxSharedMeshHeight(GameObject model)
    {
        float max = 0f;
        foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null || smr.sharedMesh == null)
                continue;
            Vector3 sz = smr.sharedMesh.bounds.size;
            max = Mathf.Max(max, sz.x, sz.y, sz.z);
        }
        foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null)
                continue;
            Vector3 sz = mf.sharedMesh.bounds.size;
            max = Mathf.Max(max, sz.x, sz.y, sz.z);
        }
        return max;
    }

    static Bounds GetWorldVisualBounds(GameObject model)
    {
        bool any = false;
        Bounds b = new Bounds(model.transform.position, Vector3.zero);

        foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null)
                continue;
            smr.updateWhenOffscreen = true;
            Bounds rb = smr.bounds;
            if (rb.size.sqrMagnitude < 1e-8f)
                continue;
            if (!any)
            {
                b = rb;
                any = true;
            }
            else b.Encapsulate(rb);
        }

        // Bone span catches Kenny ×100 where renderer AABB is still wrong in edit mode.
        var anim = model.GetComponentInChildren<Animator>();
        if (anim != null)
        {
            var bones = new[]
            {
                HumanBodyBones.Head, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
                HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.Hips
            };
            foreach (var bone in bones)
            {
                Transform t = null;
                try { t = anim.GetBoneTransform(bone); } catch { /* no avatar */ }
                if (t == null)
                    continue;
                if (!any)
                {
                    b = new Bounds(t.position, Vector3.zero);
                    any = true;
                }
                else b.Encapsulate(t.position);
            }
        }

        // Named bone tips (Kenny has no humanoid avatar).
        string[] tipNames = { "Head_end", "Head", "LeftToes_end", "RightToes_end", "LeftFoot", "RightFoot", "Hips" };
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
        {
            if (t == null)
                continue;
            bool match = false;
            for (int i = 0; i < tipNames.Length; i++)
            {
                if (t.name == tipNames[i])
                {
                    match = true;
                    break;
                }
            }
            if (!match)
                continue;
            if (!any)
            {
                b = new Bounds(t.position, Vector3.zero);
                any = true;
            }
            else b.Encapsulate(t.position);
        }

        if (!any)
            return GetWorldMeshBounds(model);

        // If bone span dwarfs mesh AABB, trust bones (the Kenny ×100 case).
        Bounds mesh = GetWorldMeshBounds(model);
        if (mesh.size.y > 1e-4f && b.size.y > mesh.size.y * 5f)
            return b;
        if (mesh.size.y > b.size.y)
            return mesh;
        return b;
    }

    static Bounds GetWorldMeshBounds(GameObject model)
    {
        bool any = false;
        Bounds b = new Bounds(model.transform.position, Vector3.zero);

        foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null || smr.sharedMesh == null)
                continue;
            EncapsulateTransformedBounds(ref b, ref any, smr.sharedMesh.bounds, smr.transform.localToWorldMatrix);
        }

        foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null)
                continue;
            EncapsulateTransformedBounds(ref b, ref any, mf.sharedMesh.bounds, mf.transform.localToWorldMatrix);
        }

        // Fallback: renderer AABB (may be wrong for un-animated skinned meshes).
        if (!any)
        {
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null)
                    continue;
                if (!any)
                {
                    b = r.bounds;
                    any = true;
                }
                else b.Encapsulate(r.bounds);
            }
        }

        return b;
    }

    static void EncapsulateTransformedBounds(ref Bounds b, ref bool any, Bounds localMesh, Matrix4x4 localToWorld)
    {
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3(
                (i & 1) == 0 ? localMesh.min.x : localMesh.max.x,
                (i & 2) == 0 ? localMesh.min.y : localMesh.max.y,
                (i & 4) == 0 ? localMesh.min.z : localMesh.max.z);
            Vector3 world = localToWorld.MultiplyPoint3x4(corner);
            if (!any)
            {
                b = new Bounds(world, Vector3.zero);
                any = true;
            }
            else b.Encapsulate(world);
        }
    }

    static Bounds GetLocalRendererBounds(GameObject model)
    {
        Bounds world = GetWorldMeshBounds(model);
        bool any = false;
        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3(
                (i & 1) == 0 ? world.min.x : world.max.x,
                (i & 2) == 0 ? world.min.y : world.max.y,
                (i & 4) == 0 ? world.min.z : world.max.z);
            Vector3 local = model.transform.InverseTransformPoint(corner);
            if (!any)
            {
                b = new Bounds(local, Vector3.zero);
                any = true;
            }
            else b.Encapsulate(local);
        }
        return b;
    }

    static void ApplyAiStats(GameObject prefabAsset, float speed)
    {
        var ai = prefabAsset.GetComponent<ZombieAI>();
        if (ai == null)
            return;
        ApplyAiStatsOnComponent(ai, speed);
        EditorUtility.SetDirty(prefabAsset);
    }

    static void ApplyAiStatsOnComponent(ZombieAI ai, float speed)
    {
        if (ai == null)
            return;
        BalanceFromSpeed(speed, out int minHp, out int maxHp, out int damage);
        ai.moveSpeed = speed;
        ai.minShotsToKill = minHp;
        ai.maxShotsToKill = maxHp;
        ai.maxHealth = maxHp;
        ai.attackDamage = damage;
        ai.detectionRadius = 25f;
        ai.attackRadius = 2f;
    }

    static AnimatorController GetOrCreateClipController(string id, string idleFbx, string runFbx)
    {
        string path = $"{AnimFolder}/Zombie_{id}.controller";
        var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        if (existing != null)
        {
            // Refresh motions in case clips were missing on first create.
            WireIdleRun(existing, id, idleFbx, runFbx);
            return existing;
        }

        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        if (ctrl.parameters.Length == 0 || System.Array.FindIndex(ctrl.parameters, p => p.name == "Speed") < 0)
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);

        var sm = ctrl.layers[0].stateMachine;
        // Clear default empty state if present.
        while (sm.states.Length > 0)
            sm.RemoveState(sm.states[0].state);

        var idle = sm.AddState("Idle", new Vector3(250, 0, 0));
        sm.defaultState = idle;
        var walk = sm.AddState("Walk", new Vector3(500, 0, 0));
        var toWalk = idle.AddTransition(walk);
        toWalk.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        toWalk.hasExitTime = false;
        toWalk.duration = 0.15f;
        var toIdle = walk.AddTransition(idle);
        toIdle.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
        toIdle.hasExitTime = false;
        toIdle.duration = 0.15f;

        WireIdleRun(ctrl, id, idleFbx, runFbx);
        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    static void WireIdleRun(AnimatorController ctrl, string id, string idleFbx, string runFbx)
    {
        // Sanitized copies keep the FBX Root bind-pose (−90° X). Raw Kenny Generic clips
        // overwrite that rotation and leave the mesh on its back.
        AnimationClip idleClip = GetOrCreateUprightClip(id, "Idle", idleFbx);
        AnimationClip runClip = GetOrCreateUprightClip(id, "Run", runFbx);
        var sm = ctrl.layers[0].stateMachine;
        foreach (var st in sm.states)
        {
            if (st.state.name == "Idle" && idleClip != null)
                st.state.motion = idleClip;
            if (st.state.name == "Walk" && runClip != null)
                st.state.motion = runClip;
        }
        EditorUtility.SetDirty(ctrl);
    }

    [MenuItem("Tools/Stargrave/Diagnose Kenny Zombie Animation")]
    public static void DiagnoseKennyZombieAnimation()
    {
        // Re-bake Walker run so diagnosis uses a freshly authored legacy clip.
        string runFbx = "Assets/ThirdParty/Kenny/Charecters/Survivors/Animations/run.fbx";
        var clip = GetOrCreateUprightClip("Walker", "Run", runFbx, asLegacy: false);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabFolder}/Zombie_Walker.prefab");
        if (clip == null || prefab == null)
        {
            Debug.LogError("[ZombieSetup] Diagnose failed: missing Walker run clip or prefab.");
            return;
        }

        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        try
        {
            var model = go.transform.Find("CharacterModel");
            var leg = model != null ? model.Find("Root/HipsCtrl/Hips/LeftUpLeg") : null;
            if (model == null || leg == null)
            {
                Debug.LogError("[ZombieSetup] Diagnose failed: CharacterModel/.../LeftUpLeg missing.");
                return;
            }

            var ai = go.GetComponent<ZombieAI>();
            // Force-assign in case prefab refs were orphaned by a prior DeleteAsset bake.
            if (ai != null)
            {
                ai.locomotionRun = clip;
                ai.locomotionIdle = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{AnimFolder}/Zombie_Walker_Idle_Upright.anim");
            }

            int bindings = AnimationUtility.GetCurveBindings(clip).Length;
            float curveSpread = 0f;
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                if (b.path == null || b.path.IndexOf("LeftUpLeg", System.StringComparison.Ordinal) < 0)
                    continue;
                if (b.propertyName == null || b.propertyName.IndexOf("m_LocalRotation", System.StringComparison.Ordinal) < 0)
                    continue;
                AnimationCurve c = AnimationUtility.GetEditorCurve(clip, b);
                if (c == null) continue;
                curveSpread = Mathf.Max(curveSpread, Mathf.Abs(c.Evaluate(0f) - c.Evaluate(clip.length * 0.5f)));
            }

            clip.SampleAnimation(model.gameObject, 0f);
            Quaternion legA = leg.localRotation;
            clip.SampleAnimation(model.gameObject, Mathf.Max(0.01f, clip.length * 0.5f));
            float sampleOnModel = Quaternion.Angle(legA, leg.localRotation);

            float modeDelta = -1f;
            if (!AnimationMode.InAnimationMode())
                AnimationMode.StartAnimationMode();
            try
            {
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(model.gameObject, clip, 0f);
                Quaternion m0 = leg.localRotation;
                AnimationMode.SampleAnimationClip(model.gameObject, clip, clip.length * 0.5f);
                modeDelta = Quaternion.Angle(m0, leg.localRotation);
                AnimationMode.EndSampling();
            }
            finally
            {
                if (AnimationMode.InAnimationMode())
                    AnimationMode.StopAnimationMode();
            }

            // Legacy Animation component on CharacterModel
            foreach (var old in model.GetComponents<Animation>())
                Object.DestroyImmediate(old);
            var anim = model.gameObject.AddComponent<Animation>();
            anim.playAutomatically = false;
            anim.AddClip(clip, "run");
            anim.Play("run");
            anim["run"].normalizedTime = 0.1f;
            anim.Sample();
            Quaternion a0 = leg.localRotation;
            anim["run"].normalizedTime = 0.6f;
            anim.Sample();
            float animDelta = Quaternion.Angle(a0, leg.localRotation);

            Debug.Log($"[ZombieSetup] Diagnose Walker: legacy={clip.legacy}, len={clip.length:F2}, bindings={bindings}, " +
                      $"curveSpread={curveSpread:F4}, sampleOnModelΔ={sampleOnModel:F2}°, " +
                      $"animModeΔ={modeDelta:F2}°, animCompΔ={animDelta:F2}°, " +
                      $"ai={(ai != null)}, idle={(ai != null && ai.locomotionIdle != null)}, run={(ai != null && ai.locomotionRun != null)}");
            if (curveSpread < 1e-4f)
                Debug.LogError("[ZombieSetup] Curves are flat — source FBX clip bake failed.");
            else if (sampleOnModel < 0.5f && modeDelta < 0.5f && animDelta < 0.5f)
                Debug.LogError("[ZombieSetup] Curves vary but nothing applies to LeftUpLeg.");
            else
                Debug.Log("[ZombieSetup] Bone motion OK — Play Mode should animate Kenny zombies.");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    static AnimationClip GetOrCreateUprightClip(string id, string label, string fbxPath, bool asLegacy = false)
    {
        AnimationClip source = FindBestClip(fbxPath);
        if (source == null)
            return null;

        // Bake LEGACY curves via SetEditorCurve. Reuse the asset path so prefab GUIDs stay valid
        // (DeleteAsset+CreateAsset was orphaning locomotionIdle/Run references).
        _ = asLegacy;
        string outPath = $"{AnimFolder}/Zombie_{id}_{label}_Upright.anim";
        // Always recreate — flipping m_Legacy on disk is unreliable and Playables need non-legacy clips.
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(outPath) != null)
            AssetDatabase.DeleteAsset(outPath);

        var dest = new AnimationClip
        {
            name = $"Zombie_{id}_{label}_Upright",
            legacy = false,
            wrapMode = WrapMode.Loop
        };
        bool created = true;

        int copied = 0;
        foreach (var binding in AnimationUtility.GetCurveBindings(source))
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve(source, binding);
            if (curve == null)
                continue;

            EditorCurveBinding b = binding;
            if (!string.IsNullOrEmpty(b.path) && b.path != "Root" &&
                !b.path.StartsWith("Root/", System.StringComparison.Ordinal))
                b.path = "Root/" + b.path;

            if (string.IsNullOrEmpty(b.path))
            {
                // Empty path = Animator host. Strip transform curves (same as playable bake) so
                // clips cannot overwrite FitModelHeight with Kenny ×100.
                string prop = b.propertyName ?? "";
                if (prop.IndexOf("Rotation", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prop.IndexOf("Euler", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prop.IndexOf("Scale", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prop.IndexOf("Position", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prop.IndexOf("RootQ", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prop.IndexOf("RootT", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prop.IndexOf("MotionT", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prop.IndexOf("MotionQ", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
            }

            AnimationUtility.SetEditorCurve(dest, b, curve);
            copied++;
        }

        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(source))
        {
            ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(source, binding);
            if (keys == null || keys.Length == 0)
                continue;
            EditorCurveBinding b = binding;
            if (!string.IsNullOrEmpty(b.path) && b.path != "Root" &&
                !b.path.StartsWith("Root/", System.StringComparison.Ordinal))
                b.path = "Root/" + b.path;
            AnimationUtility.SetObjectReferenceCurve(dest, b, keys);
        }

        AssetDatabase.CreateAsset(dest, outPath);
        AssetDatabase.SaveAssets();
        var loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(outPath);
        if (loaded != null && loaded.legacy)
        {
            // Last resort: force serialize as mecanim.
            loaded.legacy = false;
            EditorUtility.SetDirty(loaded);
            AssetDatabase.ForceReserializeAssets(new[] { outPath });
            AssetDatabase.SaveAssets();
            loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(outPath);
        }
        Debug.Log($"[ZombieSetup] Baked playable clip {outPath} ({copied} curves, legacy={loaded != null && loaded.legacy}, created={created}).");
        return loaded;
    }


    static AnimationClip StripRootPrefixForTest(AnimationClip source)
    {
        if (source == null)
            return null;
        var dest = new AnimationClip { name = source.name + "_NoRoot", legacy = true, wrapMode = WrapMode.Loop };
        foreach (var binding in AnimationUtility.GetCurveBindings(source))
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve(source, binding);
            if (curve == null)
                continue;
            EditorCurveBinding b = binding;
            if (b.path == "Root")
                b.path = "";
            else if (b.path != null && b.path.StartsWith("Root/", System.StringComparison.Ordinal))
                b.path = b.path.Substring("Root/".Length);
            AnimationUtility.SetEditorCurve(dest, b, curve);
        }
        return dest;
    }

    static void RemapClipPathsUnderRoot(AnimationClip clip)
    {
        if (clip == null)
            return;

        var bindings = AnimationUtility.GetCurveBindings(clip);
        foreach (var binding in bindings)
        {
            if (string.IsNullOrEmpty(binding.path) || binding.path == "Root" ||
                binding.path.StartsWith("Root/", System.StringComparison.Ordinal))
                continue;

            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null)
                continue;
            AnimationUtility.SetEditorCurve(clip, binding, null);
            var remapped = binding;
            remapped.path = "Root/" + binding.path;
            AnimationUtility.SetEditorCurve(clip, remapped, curve);
        }

        var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
        foreach (var binding in objBindings)
        {
            if (string.IsNullOrEmpty(binding.path) || binding.path == "Root" ||
                binding.path.StartsWith("Root/", System.StringComparison.Ordinal))
                continue;

            var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
            AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
            var remapped = binding;
            remapped.path = "Root/" + binding.path;
            AnimationUtility.SetObjectReferenceCurve(clip, remapped, keys);
        }
    }

    static void StripEmptyPathOrientation(AnimationClip clip)
    {
        if (clip == null)
            return;
        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            if (!string.IsNullOrEmpty(binding.path))
                continue;
            string p = binding.propertyName ?? "";
            bool orient = p.IndexOf("Rotation", System.StringComparison.OrdinalIgnoreCase) >= 0
                          || p.IndexOf("Euler", System.StringComparison.OrdinalIgnoreCase) >= 0
                          || p.StartsWith("m_LocalPosition", System.StringComparison.Ordinal)
                          || p.StartsWith("localPosition", System.StringComparison.Ordinal)
                          || p.StartsWith("RootT", System.StringComparison.Ordinal)
                          || p.StartsWith("RootQ", System.StringComparison.Ordinal)
                          || p.StartsWith("MotionT", System.StringComparison.Ordinal)
                          || p.StartsWith("MotionQ", System.StringComparison.Ordinal);
            if (orient)
                AnimationUtility.SetEditorCurve(clip, binding, null);
        }
    }

    static AnimationClip FindBestClip(string fbxPath)
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
        AnimationClip best = null;
        foreach (var a in assets)
        {
            if (a is AnimationClip clip && !clip.name.StartsWith("__preview", System.StringComparison.Ordinal))
            {
                if (best == null || clip.length > best.length)
                    best = clip;
            }
        }
        if (best == null)
            Debug.LogWarning($"[ZombieSetup] No AnimationClip found in {fbxPath}");
        return best;
    }

    static Material GetOrCreateSkinMaterial(string id, string texturePath)
    {
        string matPath = $"{MatFolder}/Zombie_{id}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (mat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                            ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                            ?? Shader.Find("Standard");
            mat = new Material(shader) { name = $"Zombie_{id}" };
            AssetDatabase.CreateAsset(mat, matPath);
        }
        if (tex != null)
        {
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", tex);
        }
        // Kill glossy skybox reflections that turn characters silver-grey at night.
        ModelMatteLighting.MakeMatte(mat, ambientFill: ModelMatteLighting.CharacterAmbientFill);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void EnsureReadableTexture(string texturePath)
    {
        var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
        if (importer == null)
            return;
        // Not required for material assign; keep defaults. Force sRGB.
        if (importer.sRGBTexture)
            return;
        importer.sRGBTexture = true;
        importer.SaveAndReimport();
    }

    static void ApplyMaterialRecursive(GameObject root, Material mat)
    {
        if (mat == null)
            return;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                mats[i] = mat;
            r.sharedMaterials = mats;
        }
    }

    static void WireSpawner(List<ZombieSpawnVariant> built)
    {
        var spawner = Object.FindAnyObjectByType<ZombieSpawner>();
        if (spawner == null)
        {
            Debug.LogWarning("[ZombieSetup] No ZombieSpawner in the open scene — variants were built but not assigned.");
            return;
        }

        Undo.RecordObject(spawner, "Wire Kenny Zombie Variants");
        spawner.variants = new List<ZombieSpawnVariant>(built);
        if (spawner.zombiePrefab == null && built.Count > 0)
            spawner.zombiePrefab = built[0].prefab;
        // Horde sim: 10k instanced agents, 40 realized Kenny bodies.
        if (spawner.maxAliveZombies > 10000)
            spawner.maxAliveZombies = 10000;
        if (spawner.maxAliveZombies < 80)
            spawner.maxAliveZombies = 10000;
        if (spawner.zombieCount > 32)
            spawner.zombieCount = 16;
        if (spawner.respawnsPerKill < 2)
            spawner.respawnsPerKill = 10;
        if (spawner.respawnsPerKill > 10)
            spawner.respawnsPerKill = 10;
        if (spawner.maintainCheckIntervalSeconds < 1f)
            spawner.maintainCheckIntervalSeconds = 3f;
        EditorUtility.SetDirty(spawner);
        EditorSceneManager.MarkSceneDirty(spawner.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
    }
}

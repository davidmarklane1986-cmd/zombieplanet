#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Builds Kenny human playable prefabs + ScriptableObject defs into Resources for character select.
/// Menu: Tools/Stargrave/Build Playable Characters
/// </summary>
public static class StargravePlayableCharacterSetup
{
    const string CharactersRoot = "Assets/Stargrave/Characters";
    const string PrefabsDir = CharactersRoot + "/Prefabs";
    const string MaterialsDir = CharactersRoot + "/Materials";
    const string ControllersDir = CharactersRoot + "/Animators";
    const string ResourcesDefsDir = "Assets/Stargrave/Resources/PlayableCharacters";

    const string CowboyPrefabSource = "Assets/GAMWILL/Zombie Shooter Series Farmer Cowboy/Prefab/T_Pose.prefab";

    // Humanoid Kenny copies (see StargravePlayableHumanoidImport) â€” ThirdParty Generic stays for zombies.
    const string ProtagonistModel = StargravePlayableHumanoidImport.KennyProtagonistHumanoid;
    const string SurvivorsModel = StargravePlayableHumanoidImport.KennySurvivorsHumanoid;

    struct CharSpec
    {
        public string id;
        public string displayName;
        public string blurb;
        public string modelPath;
        public string skinPath;
        public string cowboySource; // if set, copy this prefab instead of Kenny
        public float moveSpeed;
        public float sprint;
        public float fireCooldown;
        public float reload;
        public int mag;
        public int damage;
        public int maxHealth;
        public Color accent;
        public string idleState;
        public string runState;
        public string runBackState;
        public string deathState;
    }

    static readonly CharSpec[] Specs =
    {
        new CharSpec
        {
            id = "cowboy", displayName = "Cowboy", blurb = "Baseline gunslinger. Balanced and reliable.",
            cowboySource = CowboyPrefabSource,
            moveSpeed = 8f, sprint = 1.45f, fireCooldown = 0.35f, reload = 1.5f, mag = 6, damage = 1, maxHealth = 100,
            accent = new Color(0.85f, 0.7f, 0.3f, 1f),
            idleState = "root|Idle_Menu", runState = "root|Run_Front", runBackState = "root|Run_Back", deathState = "root|Death"
        },
        new CharSpec
        {
            id = "skater", displayName = "Skater", blurb = "Quick on their feet. Slightly fragile.",
            modelPath = ProtagonistModel,
            skinPath = "Assets/ThirdParty/Kenny/Charecters/Protagonist/Skins/skaterMaleA.png",
            moveSpeed = 9.2f, sprint = 1.5f, fireCooldown = 0.32f, reload = 1.4f, mag = 6, damage = 1, maxHealth = 90,
            accent = new Color(0.35f, 0.75f, 0.95f, 1f),
            idleState = "root|Idle_Menu", runState = "root|Run_Front", runBackState = "root|Run_Back", deathState = "root|Death"
        },
        new CharSpec
        {
            id = "cyborg", displayName = "Cyborg", blurb = "Heavy hitter. Slower trigger finger.",
            modelPath = ProtagonistModel,
            skinPath = "Assets/ThirdParty/Kenny/Charecters/Protagonist/Skins/cyborgFemaleA.png",
            moveSpeed = 7.2f, sprint = 1.35f, fireCooldown = 0.42f, reload = 1.6f, mag = 6, damage = 2, maxHealth = 110,
            accent = new Color(0.55f, 0.9f, 0.55f, 1f),
            idleState = "root|Idle_Menu", runState = "root|Run_Front", runBackState = "root|Run_Back", deathState = "root|Death"
        },
        new CharSpec
        {
            id = "criminal", displayName = "Criminal", blurb = "Spray and pray. Small mag, long reload.",
            modelPath = ProtagonistModel,
            skinPath = "Assets/ThirdParty/Kenny/Charecters/Protagonist/Skins/criminalMaleA.png",
            moveSpeed = 8.4f, sprint = 1.45f, fireCooldown = 0.26f, reload = 1.8f, mag = 4, damage = 1, maxHealth = 95,
            accent = new Color(0.95f, 0.4f, 0.35f, 1f),
            idleState = "root|Idle_Menu", runState = "root|Run_Front", runBackState = "root|Run_Back", deathState = "root|Death"
        },
        new CharSpec
        {
            id = "survivor", displayName = "Survivor", blurb = "Tough as nails. A bit slower on the draw.",
            modelPath = SurvivorsModel,
            skinPath = "Assets/ThirdParty/Kenny/Charecters/Survivors/Skins/survivorMaleB.png",
            moveSpeed = 7.6f, sprint = 1.4f, fireCooldown = 0.38f, reload = 1.5f, mag = 6, damage = 1, maxHealth = 130,
            accent = new Color(0.75f, 0.55f, 0.35f, 1f),
            idleState = "root|Idle_Menu", runState = "root|Run_Front", runBackState = "root|Run_Back", deathState = "root|Death"
        },
    };

    [MenuItem("Tools/Stargrave/Build Playable Characters")]
    public static void BuildPlayableCharacters()
    {
        EnsureFolder(CharactersRoot);
        EnsureFolder(PrefabsDir);
        EnsureFolder(MaterialsDir);
        EnsureFolder(ControllersDir);
        EnsureFolder("Assets/Stargrave/Resources");
        EnsureFolder(ResourcesDefsDir);

        StargravePlayableHumanoidImport.EnsureHumanoidPipeline();

        int ok = 0;
        for (int i = 0; i < Specs.Length; i++)
        {
            CharSpec spec = Specs[i];
            GameObject prefab = null;
            if (!string.IsNullOrEmpty(spec.cowboySource))
            {
                prefab = BuildCowboyWrapper(spec);
            }
            else
            {
                prefab = BuildKennyPrefab(spec);
            }

            if (CreateOrUpdateDef(spec, prefab) != null)
                ok++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        PlayableCharacterCatalog.InvalidateCache();
        Debug.Log($"[Stargrave] Build Playable Characters finished. {ok}/{Specs.Length} defs ready under {ResourcesDefsDir}.");
    }

    static GameObject BuildCowboyWrapper(CharSpec spec)
    {
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(spec.cowboySource);
        if (source == null)
        {
            Debug.LogError($"[Stargrave] Cowboy source missing: {spec.cowboySource}");
            return null;
        }

        string path = PrefabsDir + "/Playable_Cowboy.prefab";
        GameObject root = new GameObject("Playable_Cowboy");
        try
        {
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            instance.name = source.name;
            instance.transform.SetParent(root.transform, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }

        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }


    static GameObject BuildKennyPrefab(CharSpec spec)
    {
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(spec.modelPath);
        if (model == null)
        {
            Debug.LogError($"[Stargrave] Humanoid model missing: {spec.modelPath}");
            return null;
        }

        Texture2D skin = AssetDatabase.LoadAssetAtPath<Texture2D>(spec.skinPath);
        Material mat = CreateOrUpdateSkinMaterial(spec.id, skin);
        RuntimeAnimatorController controller = CreateOrUpdateFarmerRetargetController(spec);
        string path = PrefabsDir + $"/Playable_{ToPascal(spec.id)}.prefab";

        GameObject root = new GameObject($"Playable_{ToPascal(spec.id)}");
        try
        {
            GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(model);
            visual.name = "Model";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            ApplyMaterialRecursive(visual, mat);

            foreach (var old in visual.GetComponentsInChildren<Animator>(true))
                Object.DestroyImmediate(old);
            foreach (var old in visual.GetComponentsInChildren<Animation>(true))
                Object.DestroyImmediate(old);
            foreach (var old in visual.GetComponentsInChildren<KennyLocomotionDriver>(true))
                Object.DestroyImmediate(old);

            Animator animator = visual.AddComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = controller;

            Avatar humanAvatar = StargravePlayableHumanoidImport.LoadEmbeddedAvatar(spec.modelPath);
            if (humanAvatar != null && humanAvatar.isValid)
                animator.avatar = humanAvatar;
            else
                Debug.LogWarning($"[Stargrave] No valid Humanoid avatar on {spec.modelPath} for {spec.id}");

            float targetHeight = MeasurePrefabMeshHeight(CowboyPrefabSource);
            if (targetHeight < 0.5f)
                targetHeight = 1.85f;
            FitVisualHeight(visual, targetHeight);

            var scaleLock = visual.GetComponent<FittedVisualScaleLock>();
            if (scaleLock == null)
                scaleLock = visual.AddComponent<FittedVisualScaleLock>();
            scaleLock.SetLockedScale(visual.transform.localScale);

            AttachHandGunAndMuzzle(visual, animator);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }

        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    static RuntimeAnimatorController CreateOrUpdateFarmerRetargetController(CharSpec spec)
    {
        string path = ControllersDir + $"/Playable_{ToPascal(spec.id)}.controller";
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
            AssetDatabase.DeleteAsset(path);

        AnimationClip idle = StargravePlayableHumanoidImport.LoadFirstClip(StargravePlayableHumanoidImport.FarmerIdleMenu);
        AnimationClip runFront = StargravePlayableHumanoidImport.LoadFirstClip(StargravePlayableHumanoidImport.FarmerRunFront);
        AnimationClip runBack = StargravePlayableHumanoidImport.LoadFirstClip(StargravePlayableHumanoidImport.FarmerRunBack);
        AnimationClip death = StargravePlayableHumanoidImport.LoadFirstClip(StargravePlayableHumanoidImport.FarmerDeath);

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);
        AnimatorStateMachine sm = controller.layers[0].stateMachine;
        while (sm.states.Length > 0)
            sm.RemoveState(sm.states[0].state);

        AnimatorState idleState = sm.AddState("root|Idle_Menu");
        idleState.motion = idle;
        sm.defaultState = idleState;

        AnimatorState runFrontState = sm.AddState("root|Run_Front");
        runFrontState.motion = runFront != null ? runFront : idle;

        AnimatorState runBackState = sm.AddState("root|Run_Back");
        runBackState.motion = runBack != null ? runBack : runFrontState.motion;

        AnimatorState deathState = sm.AddState("root|Death");
        deathState.motion = death != null ? death : idle;

        EditorUtility.SetDirty(controller);
        return controller;
    }

    const string BlasterGunPath = "Assets/ThirdParty/Kenny/BlasterKit/Models/FBX format/blaster-a.fbx";

    static void AttachHandGunAndMuzzle(GameObject visual, Animator animator = null)
    {
        Transform rightHand = null;
        if (animator != null && animator.isHuman)
            rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (rightHand == null)
            rightHand = FindDescendantByName(visual.transform, "RightHand");
        if (rightHand == null)
            rightHand = FindDescendantByName(visual.transform, "hand.r");
        if (rightHand == null)
        {
            Debug.LogWarning($"[Stargrave] No RightHand on {visual.name} — cannot attach gun/muzzle.");
            return;
        }

        Transform existingGun = rightHand.Find("HeldBlaster");
        if (existingGun != null)
            Object.DestroyImmediate(existingGun.gameObject);
        Transform existingMuzzle = rightHand.Find("Muzzle_Bone");
        if (existingMuzzle != null)
            Object.DestroyImmediate(existingMuzzle.gameObject);
        Transform existingGunMuzzle = rightHand.Find("GunMuzzle");
        if (existingGunMuzzle != null)
            Object.DestroyImmediate(existingGunMuzzle.gameObject);

        GameObject gunPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BlasterGunPath);
        Transform muzzleParent = rightHand;
        if (gunPrefab != null)
        {
            GameObject gun = (GameObject)PrefabUtility.InstantiatePrefab(gunPrefab);
            gun.name = "HeldBlaster";
            gun.transform.SetParent(rightHand, false);

            float parentLossy = Mathf.Max(rightHand.lossyScale.x, rightHand.lossyScale.y, rightHand.lossyScale.z);
            float inv = 1f / Mathf.Max(parentLossy, 1e-4f);

            gun.transform.localPosition = new Vector3(0.025f, 0.01f, 0.04f) * inv;
            gun.transform.localRotation = Quaternion.Euler(-90f, 180f, 90f);
            gun.transform.localScale = Vector3.one;

            Bounds wb = GetWorldRendererBounds(gun);
            float longest = Mathf.Max(wb.size.x, Mathf.Max(wb.size.y, wb.size.z));
            if (longest > 1e-4f)
                gun.transform.localScale = Vector3.one * (0.38f / longest);
            else
                gun.transform.localScale = Vector3.one * (0.01f * inv);

            foreach (var col in gun.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(col);
            muzzleParent = gun.transform;
        }

        var muzzle = new GameObject("Muzzle_Bone");
        muzzle.transform.SetParent(muzzleParent, false);
        if (gunPrefab != null)
        {
            float gunLossy = Mathf.Max(muzzleParent.lossyScale.x, 1e-4f);
            muzzle.transform.localPosition = new Vector3(0f, 0f, 0.14f / gunLossy);
        }
        else
        {
            float handLossy = Mathf.Max(rightHand.lossyScale.x, 1e-4f);
            muzzle.transform.localPosition = new Vector3(0.08f, 0.02f, 0.18f) / handLossy;
        }
        muzzle.transform.localRotation = Quaternion.identity;

        var alias = new GameObject("GunMuzzle");
        alias.transform.SetParent(muzzle.transform, false);
        alias.transform.localPosition = Vector3.zero;
        alias.transform.localRotation = Quaternion.identity;
    }

    static Bounds GetWorldRendererBounds(GameObject root)
    {
        bool any = false;
        Bounds b = new Bounds(root.transform.position, Vector3.zero);
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null)
                continue;
            if (!any)
            {
                b = r.bounds;
                any = true;
            }
            else
                b.Encapsulate(r.bounds);
        }
        return any ? b : new Bounds(root.transform.position, Vector3.one * 0.01f);
    }

    static Transform FindDescendantByName(Transform root, string exactName)
    {
        if (root == null || string.IsNullOrEmpty(exactName))
            return null;
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].name == exactName)
                return all[i];
        }
        return null;
    }

    static Material CreateOrUpdateSkinMaterial(string id, Texture2D skin)
    {
        string matPath = MaterialsDir + $"/Playable_{ToPascal(id)}_Skin.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, matPath);
        }
        else
        {
            mat.shader = shader;
        }

        if (skin != null)
        {
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", skin);
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", skin);
        }
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static PlayableCharacterDef CreateOrUpdateDef(CharSpec spec, GameObject prefab)
    {
        string path = ResourcesDefsDir + $"/Char_{ToPascal(spec.id)}.asset";
        PlayableCharacterDef def = AssetDatabase.LoadAssetAtPath<PlayableCharacterDef>(path);
        if (def == null)
        {
            def = ScriptableObject.CreateInstance<PlayableCharacterDef>();
            AssetDatabase.CreateAsset(def, path);
        }

        def.id = spec.id;
        def.displayName = spec.displayName;
        def.blurb = spec.blurb;
        def.moveSpeed = spec.moveSpeed;
        def.sprintMultiplier = spec.sprint;
        def.maxHealth = spec.maxHealth;
        def.characterPrefab = prefab;
        def.modelYawOffsetDegrees = 0f;
        def.hipsYawOffsetDegrees = spec.id == "cowboy" ? 0f : 12f;
        def.accentColor = spec.accent;
        def.idleStateName = spec.idleState;
        def.runStateName = spec.runState;
        def.runBackStateName = spec.runBackState;
        def.deathStateName = spec.deathState;

        string weaponPath = ResolveDefaultWeaponPath(spec.id);
        WeaponDef assigned = AssetDatabase.LoadAssetAtPath<WeaponDef>(weaponPath);
        if (assigned != null)
            def.assignedWeapon = assigned;

        EditorUtility.SetDirty(def);
        return def;
    }

    static string ResolveDefaultWeaponPath(string characterId)
    {
        if (string.IsNullOrEmpty(characterId))
            return "Assets/Stargrave/Resources/Weapons/Weapon_Blaster.asset";

        switch (characterId.ToLowerInvariant())
        {
            case "cowboy": return "Assets/Stargrave/Resources/Weapons/Weapon_Shotgun.asset";
            case "skater": return "Assets/Stargrave/Resources/Weapons/Weapon_Handgun.asset";
            case "cyborg": return "Assets/Stargrave/Resources/Weapons/Weapon_Rifle.asset";
            case "criminal": return "Assets/Stargrave/Resources/Weapons/Weapon_Blaster.asset";
            case "survivor": return "Assets/Stargrave/Resources/Weapons/Weapon_SMG.asset";
            default: return "Assets/Stargrave/Resources/Weapons/Weapon_Blaster.asset";
        }
    }

    static void ApplyMaterialRecursive(GameObject root, Material mat)
    {
        if (root == null || mat == null)
            return;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null)
                continue;
            var mats = new Material[r.sharedMaterials.Length];
            for (int m = 0; m < mats.Length; m++)
                mats[m] = mat;
            r.sharedMaterials = mats;
        }
    }

    static float MeasurePrefabMeshHeight(string prefabPath)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
            return 0f;
        GameObject temp = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        try
        {
            return Mathf.Max(0f, MeasureWorldMeshHeight(temp));
        }
        finally
        {
            Object.DestroyImmediate(temp);
        }
    }

    static void FitVisualHeight(GameObject visual, float targetHeight)
    {
        if (visual == null || targetHeight < 1e-3f)
            return;
        visual.transform.localScale = Vector3.one;
        float height = MeasureWorldMeshHeight(visual);
        // Kenny sharedMesh AABB can report bone-space Ã—100 sizes; reject nonsense.
        float scale;
        if (height < 0.35f || height > 4.5f)
        {
            // Same fallback as zombies: assume ~1.8m mesh with embedded Ã—100 on Root.
            scale = targetHeight / (1.8f * 100f);
            visual.transform.localScale = Vector3.one * scale;
            Debug.LogWarning($"[Stargrave] FitVisualHeight '{visual.name}': unreliable height {height:F3}m â€” using scale {scale:F4} (target {targetHeight:F3}m).");
            return;
        }
        scale = targetHeight / height;
        visual.transform.localScale = Vector3.one * scale;
        Debug.Log($"[Stargrave] FitVisualHeight '{visual.name}': {height:F3}m â†’ scale {scale:F3} (target {targetHeight:F3}m)");
    }

    static float MeasureWorldMeshHeight(GameObject root)
    {
        bool any = false;
        Bounds b = new Bounds(root.transform.position, Vector3.zero);

        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null || smr.sharedMesh == null)
                continue;
            smr.updateWhenOffscreen = true;
            Mesh baked = new Mesh();
            try
            {
                smr.BakeMesh(baked, true);
                EncapsulateMeshCorners(ref b, ref any, baked.bounds, smr.transform.localToWorldMatrix);
            }
            catch
            {
                EncapsulateMeshCorners(ref b, ref any, smr.sharedMesh.bounds, smr.transform.localToWorldMatrix);
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null)
                continue;
            EncapsulateMeshCorners(ref b, ref any, mf.sharedMesh.bounds, mf.transform.localToWorldMatrix);
        }
        if (!any)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null)
                    continue;
                if (!any)
                {
                    b = r.bounds;
                    any = true;
                }
                else
                    b.Encapsulate(r.bounds);
            }
        }
        return any ? b.size.y : 0f;
    }

    static void EncapsulateMeshCorners(ref Bounds world, ref bool any, Bounds localMesh, Matrix4x4 localToWorld)
    {
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3(
                (i & 1) == 0 ? localMesh.min.x : localMesh.max.x,
                (i & 2) == 0 ? localMesh.min.y : localMesh.max.y,
                (i & 4) == 0 ? localMesh.min.z : localMesh.max.z);
            Vector3 w = localToWorld.MultiplyPoint3x4(corner);
            if (!any)
            {
                world = new Bounds(w, Vector3.zero);
                any = true;
            }
            else
                world.Encapsulate(w);
        }
    }

    static string ToPascal(string id)
    {
        if (string.IsNullOrEmpty(id))
            return "Char";
        return char.ToUpperInvariant(id[0]) + id.Substring(1);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        string name = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        if (!string.IsNullOrEmpty(parent))
            AssetDatabase.CreateFolder(parent, name);
    }
}
#endif

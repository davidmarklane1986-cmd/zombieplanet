#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Copies Kenny models + farmer locomotion FBXs under Stargrave/Characters/Humanoid
/// and configures them as Humanoid so farmer clips retarget onto Kenny playables.
/// Leaves ThirdParty Kenny Generic (zombies) and live GAMWILL assets untouched.
/// </summary>
public static class StargravePlayableHumanoidImport
{
    public const string HumanoidRoot = "Assets/Stargrave/Characters/Humanoid";

    public const string KennyProtagonistSrc = "Assets/ThirdParty/Kenny/Charecters/Protagonist/Model/characterMedium.fbx";
    public const string KennySurvivorsSrc = "Assets/ThirdParty/Kenny/Charecters/Survivors/Model/characterMedium.fbx";
    public const string KennyRetroSrc = "Assets/ThirdParty/Kenny/Charecters/Retro/Model/characterMedium.fbx";

    public const string FarmerTPoseSrc = "Assets/GAMWILL/Zombie Shooter Series Farmer Cowboy/Mesh/T_Pose.fbx";
    public const string FarmerAnimDir = "Assets/GAMWILL/Zombie Shooter Series Farmer Cowboy/Animation";

    public const string KennyProtagonistHumanoid = HumanoidRoot + "/Kenny_Protagonist.fbx";
    public const string KennySurvivorsHumanoid = HumanoidRoot + "/Kenny_Survivors.fbx";
    public const string KennyRetroHumanoid = HumanoidRoot + "/Kenny_Retro.fbx";
    public const string FarmerTPoseHumanoid = HumanoidRoot + "/Farmer_TPose.fbx";

    public const string FarmerIdleMenu = HumanoidRoot + "/Farmer_Idle_Menu.fbx";
    public const string FarmerRunFront = HumanoidRoot + "/Farmer_Run_Front.fbx";
    public const string FarmerRunBack = HumanoidRoot + "/Farmer_Run_Back.fbx";
    public const string FarmerDeath = HumanoidRoot + "/Farmer_Death.fbx";

    public static void EnsureHumanoidPipeline()
    {
        EnsureFolder("Assets/Stargrave/Characters");
        EnsureFolder(HumanoidRoot);

        CopyIfMissing(KennyProtagonistSrc, KennyProtagonistHumanoid);
        CopyIfMissing(KennySurvivorsSrc, KennySurvivorsHumanoid);
        CopyIfMissing(KennyRetroSrc, KennyRetroHumanoid);
        CopyIfMissing(FarmerTPoseSrc, FarmerTPoseHumanoid);
        CopyIfMissing(FarmerAnimDir + "/Idle_Menu.fbx", FarmerIdleMenu);
        CopyIfMissing(FarmerAnimDir + "/Run_Front.fbx", FarmerRunFront);
        CopyIfMissing(FarmerAnimDir + "/Run_Back.fbx", FarmerRunBack);
        CopyIfMissing(FarmerAnimDir + "/Death.fbx", FarmerDeath);

        AssetDatabase.Refresh();

        ConfigureKennyHumanoidModel(KennyProtagonistHumanoid);
        ConfigureKennyHumanoidModel(KennySurvivorsHumanoid);
        ConfigureKennyHumanoidModel(KennyRetroHumanoid);
        ConfigureFarmerHumanoidModel(FarmerTPoseHumanoid);

        Avatar farmerAvatar = LoadEmbeddedAvatar(FarmerTPoseHumanoid);
        if (farmerAvatar == null || !farmerAvatar.isValid)
        {
            Debug.LogWarning("[Stargrave] Farmer Humanoid avatar missing/invalid — clip retarget may fail. Check Farmer_TPose bone map.");
        }

        ConfigureFarmerHumanoidClip(FarmerIdleMenu, farmerAvatar);
        ConfigureFarmerHumanoidClip(FarmerRunFront, farmerAvatar);
        ConfigureFarmerHumanoidClip(FarmerRunBack, farmerAvatar);
        ConfigureFarmerHumanoidClip(FarmerDeath, farmerAvatar);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        LogAvatarStatus(KennyProtagonistHumanoid, "Kenny Protagonist");
        LogAvatarStatus(KennySurvivorsHumanoid, "Kenny Survivors");
        LogAvatarStatus(KennyRetroHumanoid, "Kenny Retro");
        LogAvatarStatus(FarmerTPoseHumanoid, "Farmer TPose");
    }

    static void LogAvatarStatus(string path, string label)
    {
        Avatar av = LoadEmbeddedAvatar(path);
        if (av == null)
            Debug.LogWarning($"[Stargrave] Humanoid '{label}': no avatar at {path}");
        else
            Debug.Log($"[Stargrave] Humanoid '{label}': isValid={av.isValid} isHuman={av.isHuman} ({path})");
    }

    public static Avatar LoadEmbeddedAvatar(string assetPath)
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
        if (assets == null)
            return null;
        for (int i = 0; i < assets.Length; i++)
        {
            if (assets[i] is Avatar av)
                return av;
        }
        return null;
    }

    public static AnimationClip LoadFirstClip(string fbxPath)
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
        if (assets == null)
            return null;
        for (int i = 0; i < assets.Length; i++)
        {
            if (assets[i] is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                return clip;
        }
        return null;
    }

    static void CopyIfMissing(string src, string dest)
    {
        if (AssetDatabase.LoadAssetAtPath<Object>(dest) != null)
            return;
        if (AssetDatabase.LoadAssetAtPath<Object>(src) == null)
        {
            Debug.LogError($"[Stargrave] Humanoid copy source missing: {src}");
            return;
        }
        if (!AssetDatabase.CopyAsset(src, dest))
            Debug.LogError($"[Stargrave] Failed to copy {src} → {dest}");
    }

    static void ConfigureKennyHumanoidModel(string path)
    {
        var imp = AssetImporter.GetAtPath(path) as ModelImporter;
        if (imp == null)
            return;

        bool dirty = false;
        if (imp.animationType != ModelImporterAnimationType.Human)
        {
            imp.animationType = ModelImporterAnimationType.Human;
            dirty = true;
        }
        if (imp.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
        {
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            dirty = true;
        }
        if (!imp.autoGenerateAvatarMappingIfUnspecified)
        {
            imp.autoGenerateAvatarMappingIfUnspecified = true;
            dirty = true;
        }

        if (dirty)
            imp.SaveAndReimport();
    }

    static void ConfigureFarmerHumanoidModel(string path)
    {
        var imp = AssetImporter.GetAtPath(path) as ModelImporter;
        if (imp == null)
            return;

        imp.animationType = ModelImporterAnimationType.Human;
        imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        imp.autoGenerateAvatarMappingIfUnspecified = true;

        HumanDescription hd = imp.humanDescription;
        hd.human = BuildFarmerHumanBones();
        hd.hasTranslationDoF = false;
        hd.armStretch = 0.05f;
        hd.legStretch = 0.05f;
        hd.feetSpacing = 0f;
        imp.humanDescription = hd;
        imp.SaveAndReimport();
    }

    static void ConfigureFarmerHumanoidClip(string path, Avatar sourceAvatar)
    {
        var imp = AssetImporter.GetAtPath(path) as ModelImporter;
        if (imp == null)
            return;

        imp.animationType = ModelImporterAnimationType.Human;
        if (sourceAvatar != null && sourceAvatar.isValid)
        {
            imp.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            imp.sourceAvatar = sourceAvatar;
        }
        else
        {
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.autoGenerateAvatarMappingIfUnspecified = true;
            HumanDescription hd = imp.humanDescription;
            hd.human = BuildFarmerHumanBones();
            imp.humanDescription = hd;
        }
        imp.SaveAndReimport();
    }

    static HumanBone[] BuildFarmerHumanBones()
    {
        var list = new List<HumanBone>
        {
            HB(HumanBodyBones.Hips, "root"),
            HB(HumanBodyBones.Spine, "spine_01.x"),
            HB(HumanBodyBones.Chest, "spine_02.x"),
            HB(HumanBodyBones.UpperChest, "spine_03.x"),
            HB(HumanBodyBones.Neck, "neck.x"),
            HB(HumanBodyBones.Head, "head.x"),
            HB(HumanBodyBones.LeftShoulder, "shoulder.l"),
            HB(HumanBodyBones.RightShoulder, "shoulder.r"),
            HB(HumanBodyBones.LeftUpperArm, "arm_stretch.l"),
            HB(HumanBodyBones.RightUpperArm, "arm_stretch.r"),
            HB(HumanBodyBones.LeftLowerArm, "forearm_stretch.l"),
            HB(HumanBodyBones.RightLowerArm, "forearm_stretch.r"),
            HB(HumanBodyBones.LeftHand, "hand.l"),
            HB(HumanBodyBones.RightHand, "hand.r"),
            HB(HumanBodyBones.LeftUpperLeg, "thigh_stretch.l"),
            HB(HumanBodyBones.RightUpperLeg, "thigh_stretch.r"),
            HB(HumanBodyBones.LeftLowerLeg, "leg_stretch.l"),
            HB(HumanBodyBones.RightLowerLeg, "leg_stretch.r"),
            HB(HumanBodyBones.LeftFoot, "foot.l"),
            HB(HumanBodyBones.RightFoot, "foot.r"),
        };
        return list.ToArray();
    }

    static HumanBone HB(HumanBodyBones bone, string boneName)
    {
        return new HumanBone
        {
            humanName = bone.ToString(),
            boneName = boneName,
            limit = new HumanLimit { useDefaultValues = true }
        };
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

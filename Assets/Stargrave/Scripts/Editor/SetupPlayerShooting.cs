using UnityEngine;
using UnityEditor;

/// <summary>
/// Menu: Tools > Stargrave > Setup Player Shooting — adds PlayerShooting to the Player and assigns the Projectile prefab.
/// Ensure the scene has a GameObject tagged "Player" (your player root).
/// </summary>
public static class SetupPlayerShooting
{
    const string ProjectilePrefabPath = "Assets/Stargrave/Prefabs/Projectile.prefab";

    [MenuItem("Tools/Stargrave/Setup Player Shooting")]
    public static void Setup()
    {
        GameObject player = GameObject.FindGameObjectWithTag("Player");
        if (player == null)
        {
            Debug.LogWarning("[SetupPlayerShooting] No GameObject with tag 'Player' in the scene. Tag your player root and run again.");
            return;
        }

        var projectilePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectilePrefabPath);
        if (projectilePrefab == null)
        {
            Debug.LogWarning("[SetupPlayerShooting] Projectile prefab not found at " + ProjectilePrefabPath);
            return;
        }

        var shooting = player.GetComponent<PlayerShooting>();
        if (shooting == null)
        {
            shooting = Undo.AddComponent<PlayerShooting>(player);
            Debug.Log("[SetupPlayerShooting] Added PlayerShooting to " + player.name);
        }

        Undo.RecordObject(shooting, "Setup Player Shooting");
        shooting.projectilePrefab = projectilePrefab;
        shooting.preferCharacterModelMuzzle = true;

        if (shooting.firePoint == null)
        {
            Transform gunMuzzle = FindCharacterMuzzle(player.transform);
            if (gunMuzzle == null)
                gunMuzzle = player.transform.Find("CameraTarget/GunMuzzle");
            if (gunMuzzle == null)
                gunMuzzle = player.transform.Find("GunMuzzle");
            if (gunMuzzle != null)
            {
                shooting.firePoint = gunMuzzle;
                Debug.Log("[SetupPlayerShooting] Set fire point to the character model muzzle / weapon bone.");
            }
            else
            {
                Transform cameraTarget = player.transform.Find("CameraTarget");
                if (cameraTarget != null)
                {
                    shooting.firePoint = cameraTarget;
                    Debug.Log("[SetupPlayerShooting] Set fire point to CameraTarget (add CameraTarget/GunMuzzle for gun-side spawn).");
                }
                else
                {
                    Transform cam = player.transform.Find("Main Camera");
                    if (cam == null)
                    {
                        var c = player.GetComponentInChildren<Camera>(true);
                        if (c != null) cam = c.transform;
                    }
                    if (cam != null)
                    {
                        shooting.firePoint = cam;
                        Debug.Log("[SetupPlayerShooting] Set fire point to " + cam.name + " (no CameraTarget child found).");
                    }
                }
            }
        }

        EditorUtility.SetDirty(shooting);
        Debug.Log("[SetupPlayerShooting] Player shooting ready. Fire: left mouse or gamepad RT (with projectile prefab = visible orb).");
    }

    static Transform FindCharacterMuzzle(Transform playerRoot)
    {
        if (playerRoot == null)
            return null;

        Transform modelRoot = playerRoot.Find("CharacterModel");
        Transform searchRoot = modelRoot != null ? modelRoot : playerRoot;

        Transform muzzle = FindDescendantByName(searchRoot, "Muzzle_Bone");
        if (muzzle != null)
            return muzzle;

        Transform weaponBone = FindDescendantByName(searchRoot, "Weapon_Bone");
        if (weaponBone != null)
            return weaponBone;

        Animator anim = searchRoot.GetComponentInChildren<Animator>(true);
        if (anim != null && anim.isHuman)
        {
            Transform rightHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (rightHand != null)
                return rightHand;
        }

        return FindDescendantByName(searchRoot, "GunMuzzle");
    }

    static Transform FindDescendantByName(Transform root, string exactName)
    {
        if (root == null || string.IsNullOrEmpty(exactName))
            return null;

        Transform[] children = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            Transform child = children[i];
            if (child != null && child.name == exactName)
                return child;
        }

        return null;
    }
}

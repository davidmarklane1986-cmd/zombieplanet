using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor hooks for MCP-driven play testing (menu start, player nudge).
/// </summary>
public static class StargraveMcpPlayTest
{
    [MenuItem("Tools/Stargrave/MCP Play Test/Prepare Auto-Start Run")]
    public static void PrepareAutoStartRun()
    {
        StargraveFrontendBootstrap.AutoStartNextBoot();
        Debug.Log("[MCP PlayTest] Next play session will skip menu and start run.");
    }

    [MenuItem("Tools/Stargrave/MCP Play Test/Start Run Now (Play Mode)")]
    public static void StartRunNow()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[MCP PlayTest] Enter Play mode first.");
            return;
        }

        var bootstrap = Object.FindFirstObjectByType<StargraveFrontendBootstrap>();
        if (bootstrap == null)
        {
            Debug.LogWarning("[MCP PlayTest] StargraveFrontendBootstrap not found.");
            return;
        }

        bootstrap.McpStartRun();
    }

    [MenuItem("Tools/Stargrave/MCP Play Test/Nudge Player Forward")]
    public static void NudgePlayerForward()
    {
        if (!EditorApplication.isPlaying)
            return;

        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null)
        {
            Debug.LogWarning("[MCP PlayTest] Player tag not found.");
            return;
        }

        Vector3 up = (player.transform.position - Vector3.zero).normalized;
        Vector3 forward = Vector3.ProjectOnPlane(player.transform.forward, up).normalized;
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.Cross(up, player.transform.right).normalized;

        player.transform.position += forward * 8f;
        Debug.Log($"[MCP PlayTest] Nudged player to {player.transform.position}");
    }
}

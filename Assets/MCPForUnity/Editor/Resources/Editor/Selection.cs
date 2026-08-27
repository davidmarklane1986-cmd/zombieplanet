using System;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Resources.Editor
{
    /// <summary>
    /// Provides detailed information about the current editor selection.
    /// </summary>
    [McpForUnityResource("get_selection")]
    public static class Selection
    {
        public static object HandleCommand(JObject @params)
        {
            try
            {
                var selectionInfo = new
                {
                    activeObject = UnityEditor.Selection.activeObject?.name,
                    activeGameObject = UnityEditor.Selection.activeGameObject?.name,
                    activeTransform = UnityEditor.Selection.activeTransform?.name,
                    activeInstanceID = GetActiveInstanceIDCompat(),
#if UNITY_6000_5_OR_NEWER
                    activeEntityID = EntityId.ToULong(UnityEditor.Selection.activeEntityId).ToString(),
#endif
                    count = UnityEditor.Selection.count,
                    objects = UnityEditor.Selection.objects
                        .Select(obj => new
                        {
                            name = obj?.name,
                            type = obj?.GetType().FullName,
                            instanceID = obj?.GetInstanceIDCompat()
                        })
                        .ToList(),
                    gameObjects = UnityEditor.Selection.gameObjects
                        .Select(go => new
                        {
                            name = go?.name,
                            instanceID = go?.GetInstanceIDCompat()
                        })
                        .ToList(),
                    assetGUIDs = UnityEditor.Selection.assetGUIDs
                };

                return new SuccessResponse("Retrieved current selection details.", selectionInfo);
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error getting selection: {e.Message}");
            }
        }

        static int GetActiveInstanceIDCompat()
        {
#if UNITY_6000_5_OR_NEWER
            return (int)EntityId.ToULong(UnityEditor.Selection.activeEntityId);
#else
            return UnityEditor.Selection.activeInstanceID;
#endif
        }
    }
}

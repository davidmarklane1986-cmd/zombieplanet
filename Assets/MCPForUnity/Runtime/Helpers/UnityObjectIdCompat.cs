using UnityEngine;
#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
#endif

namespace MCPForUnity.Runtime.Helpers
{
    /// <summary>
    /// Version-gated wrappers for the InstanceID ↔ EntityId migration (Unity 6.5+).
    /// </summary>
    public static class UnityObjectIdCompat
    {
        /// <summary>
        /// Session-scoped int handle. On 6.5+ truncates EntityId's ulong; lossy but
        /// preserves the int-shaped wire format older MCP consumers expect.
        /// </summary>
        public static int GetInstanceIDCompat(this Object obj)
        {
            if (obj == null)
            {
                return 0;
            }

#if UNITY_6000_5_OR_NEWER
            return (int)EntityId.ToULong(obj.GetEntityId());
#else
            return obj.GetInstanceID();
#endif
        }

#if UNITY_EDITOR
#if UNITY_6000_6_OR_NEWER
        private static MethodInfo _instanceIdToObject;
        private static bool _instanceIdToObjectInitialized;
#endif

        /// <summary>
        /// Resolves an int instance ID handle back to a UnityEngine.Object.
        /// </summary>
        public static Object InstanceIDToObjectCompat(int instanceId)
        {
#if UNITY_6000_6_OR_NEWER
            if (!_instanceIdToObjectInitialized)
            {
                _instanceIdToObject = typeof(EditorUtility).GetMethod(
                    "InstanceIDToObject",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int) },
                    null);
                _instanceIdToObjectInitialized = true;
            }
            return _instanceIdToObject?.Invoke(null, new object[] { instanceId }) as Object;
#elif UNITY_6000_3_OR_NEWER
            return EditorUtility.EntityIdToObject(instanceId);
#else
            return EditorUtility.InstanceIDToObject(instanceId);
#endif
        }
#endif
    }
}

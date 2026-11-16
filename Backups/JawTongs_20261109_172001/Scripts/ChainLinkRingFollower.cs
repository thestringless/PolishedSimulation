using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Sefpro.JawTongs
{
    /// <summary>
    /// Keeps the central lifting ring interlocked with one link of the Obi chain rope.
    /// The Obi chain renderer draws its links as GPU instances (no GameObjects), so the ring reads the
    /// per-link instance matrix and holds a fixed pose in that link's local frame. The ring therefore
    /// stays threaded through the link opening however the chain sags, tilts or twists.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(500)]
    public class ChainLinkRingFollower : MonoBehaviour
    {
        [Tooltip("Obi rope whose chain-link instances the ring is threaded through.")]
        public Obi.ObiRope rope;

        [Tooltip("Index of the chain link (element) the ring passes through.")]
        public int linkIndex = 12;

        [Tooltip("Ring centre in the chain link's local frame (metres, link X = width, Y = opening axis, Z = chain tangent).")]
        public Vector3 localPosition = new Vector3(-0.05495f, 0f, 0f);

        private static readonly BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        private static FieldInfo stackField;
        private static FieldInfo transformsField;
        private static FieldInfo solverStackField;

        private Obi.ObiChainRopeRenderSystem system;

        private void LateUpdate()
        {
            if (rope == null || rope.solver == null)
                return;

            if (!TryGetLinkMatrix(out Matrix4x4 m))
                return;

            Vector3 zAxis = m.GetColumn(2);
            Vector3 yAxis = m.GetColumn(1);
            if (zAxis.sqrMagnitude < 1e-8f)
                return;

            Quaternion linkRot = Quaternion.LookRotation(zAxis.normalized, yAxis.normalized);
            // Ring mesh axis (local Y) lies along the link's chain tangent (local Z).
            Quaternion ringRot = linkRot * Quaternion.FromToRotation(Vector3.up, Vector3.forward);
            transform.SetPositionAndRotation(m.MultiplyPoint3x4(localPosition), ringRot);
        }

        private bool TryGetLinkMatrix(out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            try
            {
                if (solverStackField == null)
                    solverStackField = typeof(Obi.ObiSolver).GetField("m_RenderSystems", BF);
                object stack = solverStackField?.GetValue(rope.solver);
                if (stack == null)
                    return false;

                if (stackField == null)
                    stackField = stack.GetType().GetField("stack", BF);
                var lists = stackField?.GetValue(stack) as List<Obi.IRenderSystem>[];
                if (lists == null)
                    return false;

                Obi.ObiChainRopeRenderSystem found = null;
                for (int i = 0; i < lists.Length && found == null; i++)
                {
                    if (lists[i] == null) continue;
                    foreach (var s in lists[i])
                        if (s is Obi.ObiChainRopeRenderSystem c) { found = c; break; }
                }
                if (found == null)
                    return false;
                system = found;

                if (transformsField == null)
                    transformsField = typeof(Obi.ObiChainRopeRenderSystem).GetField("instanceTransforms", BF);
                var list = transformsField?.GetValue(system) as Obi.ObiNativeList<Matrix4x4>;
                if (list == null || linkIndex < 0 || linkIndex >= list.count)
                    return false;

                matrix = list[linkIndex];
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

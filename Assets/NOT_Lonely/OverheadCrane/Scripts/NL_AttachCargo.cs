using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
[ExecuteInEditMode]
#endif

public class NL_AttachCargo : MonoBehaviour
{
    public Rigidbody rbCurrent;
    private Rigidbody rbTarget;
    private ConfigurableJoint confJoint;
    private FixedJoint fixedJoint;
    private CapsuleCollider trigger;

    public Collider[] excludeTriggers;
    private bool excludedTrigger;

    private bool inZone = false;

    public KeyCode attachKey = KeyCode.F;

    public enum ConnectionType
    {
        FixedJoint,
        ConfigurableJoint
    }
    public ConnectionType connectionType = ConnectionType.ConfigurableJoint;

    [Tooltip("When using a Configurable Joint connection type this value will be used as an offset for the connection point.")]
    public float connectPointOffset = 0.2984f;
    private float posY;
    [HideInInspector] public bool isAttached = false;
    [Header("Hopper Lifting Target")]
    public LiquidSolidPrototype.Core.HopperCraneLiftingPoint hopperLiftingTarget;

    [Header("Jaw Tongs Lifting Target")]
    public Sefpro.JawTongs.JawTongsCraneLiftingPoint tongsLiftingTarget;

    [Header("Mould Lifting Target")]
    public Sefpro.Assembly.MoldCraneLiftingPoint moldLiftingTarget;

    void Start()
    {
        if (Application.isPlaying)
        {
            trigger = gameObject.AddComponent<CapsuleCollider>();
            trigger.radius = 0.08f;
            trigger.height = 0.28f;
            trigger.center = new Vector3(0, -0.30f, 0);
            trigger.isTrigger = true;
        }
    }

    void Update()
    {
        if (Application.isPlaying)
        {
            if (hopperLiftingTarget == null)
            {
                hopperLiftingTarget = FindAnyObjectByType<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>();
            }
            if (tongsLiftingTarget == null)
            {
                tongsLiftingTarget = FindAnyObjectByType<Sefpro.JawTongs.JawTongsCraneLiftingPoint>();
            }
            if (moldLiftingTarget == null)
            {
                moldLiftingTarget = FindAnyObjectByType<Sefpro.Assembly.MoldCraneLiftingPoint>();
            }

            if (!isAttached)
            {
                Vector3 hookTip = hopperLiftingTarget != null
                    ? hopperLiftingTarget.SaddleWorld(transform)
                    : (transform.position - Vector3.up * connectPointOffset);

                if (hopperLiftingTarget != null &&
                    (tongsLiftingTarget == null || !tongsLiftingTarget.IsAttached) &&
                    (moldLiftingTarget == null || !moldLiftingTarget.IsAttached))
                {
                    float dist = Vector3.Distance(hookTip, hopperLiftingTarget.CrossbarWorldPosition);
                    if (dist <= 0.90f)
                    {
                        inZone = true;
                        hopperLiftingTarget.SetHookInRange(true, transform, rbCurrent);
                        // Controlled proximity-assisted capture within 0.50m
                        if (hopperLiftingTarget.TryAutoConnect(transform, rbCurrent))
                        {
                            isAttached = hopperLiftingTarget.IsAttached;
                        }
                    }
                    else if (hopperLiftingTarget.IsHookInRange)
                    {
                        hopperLiftingTarget.SetHookInRange(false);
                    }
                }

                if (!isAttached && (hopperLiftingTarget == null || !hopperLiftingTarget.IsCapturing) && tongsLiftingTarget != null)
                {
                    Vector3 tongsTip = transform.position - Vector3.up * connectPointOffset;
                    float distTongs = Vector3.Distance(tongsTip, tongsLiftingTarget.RingSeatWorldPosition);
                    if (distTongs <= 0.60f)
                    {
                        inZone = true;
                        tongsLiftingTarget.SetHookInRange(true, transform, rbCurrent);
                        if (distTongs <= 0.28f)
                        {
                            Vector3 offset = tongsTip - tongsLiftingTarget.RingSeatWorldPosition;
                            float horiz = new Vector2(offset.x, offset.z).magnitude;
                            if (horiz <= 0.22f && offset.y >= -0.18f && offset.y <= 0.22f)
                            {
                                isAttached = tongsLiftingTarget.AttachToHook(transform, rbCurrent, enforceSeatingTolerance: true);
                            }
                        }
                    }
                    else if (tongsLiftingTarget.IsHookInRange)
                    {
                        tongsLiftingTarget.SetHookInRange(false);
                    }
                }

                if (!isAttached && (hopperLiftingTarget == null || !hopperLiftingTarget.IsCapturing) && moldLiftingTarget != null)
                {
                    Vector3 moldTip = moldLiftingTarget.GetHookSaddleWorld(transform);
                    float distMold = Vector3.Distance(moldTip, moldLiftingTarget.RingSeatWorldPosition);
                    if (distMold <= 0.65f)
                    {
                        inZone = true;
                        moldLiftingTarget.SetHookInRange(true, transform, rbCurrent);
                        if (moldLiftingTarget.TryAutoConnect(transform, rbCurrent))
                        {
                            isAttached = moldLiftingTarget.IsAttached;
                        }
                    }
                    else if (moldLiftingTarget.IsHookInRange || !moldLiftingTarget.IsAttached)
                    {
                        moldLiftingTarget.SetHookInRange(false, transform, rbCurrent);
                    }
                }

                if ((hopperLiftingTarget == null || !hopperLiftingTarget.IsHookInRange) &&
                    (tongsLiftingTarget == null || !tongsLiftingTarget.IsHookInRange) &&
                    (moldLiftingTarget == null || !moldLiftingTarget.IsHookInRange) &&
                    rbTarget == null)
                {
                    inZone = false;
                }
            }

            // Keep the cargo flag consistent with the hopper lifting point (single source of truth for hopper connection state).
            if (hopperLiftingTarget != null)
            {
                if (!isAttached && hopperLiftingTarget.IsAttached)
                    isAttached = true;
                else if (isAttached && !hopperLiftingTarget.IsAttached &&
                         (tongsLiftingTarget == null || !tongsLiftingTarget.IsAttached) &&
                         (moldLiftingTarget == null || !moldLiftingTarget.IsAttached))
                    isAttached = false;
            }
            if (moldLiftingTarget != null && !isAttached && moldLiftingTarget.IsAttached)
            {
                isAttached = true;
            }
        }

        if (Application.isEditor)
        {
            if (rbCurrent != null)
            {
                if (rbCurrent.transform.position.y >= 0)
                {
                    posY = rbCurrent.transform.position.y - connectPointOffset;
                }
                else
                {
                    posY = rbCurrent.transform.position.y + connectPointOffset;
                }
            }
        }
    }

    /// <summary>
    /// Hoist-up gate used by NL_OverheadCrane.MoveHookUp. While a hopper is actively capturing or
    /// connected in its 0.75s confirmation delay, the hook may not be raised prematurely.
    /// </summary>
    public bool IsHoistUpAllowed()
    {
        if (hopperLiftingTarget != null && (hopperLiftingTarget.IsCapturing || hopperLiftingTarget.IsLiftBlockedWhileConnected))
            return false;
        return true;
    }

    public void ToggleAttach()
    {
        if (!isAttached)
        {
            Attach();
        }
        else
        {
            Detach();
        }
    }

    public void Attach()
    {
        if (isAttached)
            return;

        if (tongsLiftingTarget != null && tongsLiftingTarget.IsHookInRange)
        {
            isAttached = tongsLiftingTarget.AttachToHook(transform, rbCurrent);
            if (isAttached)
                return;
        }

        if (moldLiftingTarget != null && moldLiftingTarget.IsHookInRange)
        {
            isAttached = moldLiftingTarget.AttachToHook(transform, rbCurrent);
            if (isAttached)
                return;
        }

        if (hopperLiftingTarget != null && hopperLiftingTarget.IsHookInRange)
        {
            isAttached = hopperLiftingTarget.AttachToHook(transform, rbCurrent);
            if (isAttached)
                return;
        }

        return;
    }

    public void Detach()
    {
        if (!isAttached)
            return;

        if (tongsLiftingTarget != null && tongsLiftingTarget.IsAttached)
        {
            tongsLiftingTarget.DetachFromHook();
            isAttached = false;
            return;
        }

        if (moldLiftingTarget != null && moldLiftingTarget.IsAttached)
        {
            moldLiftingTarget.DetachFromHook();
            isAttached = false;
            return;
        }

        if (hopperLiftingTarget != null && hopperLiftingTarget.IsAttached)
        {
            hopperLiftingTarget.DetachFromHook();
            isAttached = false;
            return;
        }

        if (connectionType == ConnectionType.ConfigurableJoint)
        {
            if (confJoint != null)
                Destroy(confJoint);
        }
        else
        {
            if (fixedJoint != null)
                Destroy(fixedJoint);
        }

        isAttached = false;
    }

    public bool TryGetPayloadMinHookHeight(float currentHookHeight, float craneHeight, float hookOffset, out float minHookHeight)
    {
        minHookHeight = 0f;

        if (tongsLiftingTarget != null && tongsLiftingTarget.IsAttached)
        {
            return tongsLiftingTarget.TryGetMinAllowedHookHeight(currentHookHeight, craneHeight, hookOffset, out minHookHeight);
        }

        if (moldLiftingTarget != null && moldLiftingTarget.IsAttached)
        {
            return moldLiftingTarget.TryGetMinAllowedHookHeight(currentHookHeight, craneHeight, hookOffset, out minHookHeight);
        }

        if (hopperLiftingTarget != null)
        {
            return hopperLiftingTarget.TryGetMinAllowedHookHeight(currentHookHeight, craneHeight, hookOffset, out minHookHeight);
        }

        return false;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (isAttached)
            return;

        excludedTrigger = false;

        for (int i = 0; i < excludeTriggers.Length; i++)
        {
            if(other == excludeTriggers[i])
            {
                excludedTrigger = true;
                break;
            }
        }

        if (excludedTrigger)
            return;

        var tongsLift = other.GetComponent<Sefpro.JawTongs.JawTongsCraneLiftingPoint>() ?? other.GetComponentInParent<Sefpro.JawTongs.JawTongsCraneLiftingPoint>();
        if (tongsLift != null)
        {
            tongsLiftingTarget = tongsLift;
            tongsLiftingTarget.SetHookInRange(true, transform, rbCurrent);
            inZone = true;
            isAttached = tongsLiftingTarget.AttachToHook(transform, rbCurrent, enforceSeatingTolerance: true);
            return;
        }

        var moldLift = other.GetComponent<Sefpro.Assembly.MoldCraneLiftingPoint>() ?? other.GetComponentInParent<Sefpro.Assembly.MoldCraneLiftingPoint>();
        if (moldLift != null)
        {
            moldLiftingTarget = moldLift;
            moldLiftingTarget.SetHookInRange(true, transform, rbCurrent);
            inZone = true;
            isAttached = moldLiftingTarget.AttachToHook(transform, rbCurrent, enforceSeatingTolerance: false);
            return;
        }

        var hopperLift = other.GetComponent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>() ?? other.GetComponentInParent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>();
        if (hopperLift != null)
        {
            hopperLiftingTarget = hopperLift;
            hopperLiftingTarget.SetHookInRange(true, transform, rbCurrent);
            inZone = true;
            return;
        }

        if (other.name.Contains("Hopper") || (other.transform.root != null && other.transform.root.name.Contains("Hopper")))
        {
            return;
        }
        
        inZone = true;

        if (other.GetComponent<Rigidbody>())
        {
            rbTarget = other.GetComponent<Rigidbody>();
        }
    }

    private void OnTriggerExit(Collider other)
    {
        excludedTrigger = false;

        for (int i = 0; i < excludeTriggers.Length; i++)
        {
            if (other == excludeTriggers[i])
            {
                excludedTrigger = true;
                break;
            }
        }

        if (excludedTrigger)
            return;

        var tongsLift = other.GetComponent<Sefpro.JawTongs.JawTongsCraneLiftingPoint>() ?? other.GetComponentInParent<Sefpro.JawTongs.JawTongsCraneLiftingPoint>();
        if (tongsLift != null && tongsLift == tongsLiftingTarget)
        {
            tongsLiftingTarget.SetHookInRange(false);
            inZone = false;
            return;
        }

        var moldLift = other.GetComponent<Sefpro.Assembly.MoldCraneLiftingPoint>() ?? other.GetComponentInParent<Sefpro.Assembly.MoldCraneLiftingPoint>();
        if (moldLift != null && moldLift == moldLiftingTarget)
        {
            moldLiftingTarget.SetHookInRange(false, transform, rbCurrent);
            inZone = false;
            return;
        }

        var hopperLift = other.GetComponent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>() ?? other.GetComponentInParent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>();
        if (hopperLift != null && hopperLift == hopperLiftingTarget)
        {
            hopperLiftingTarget.SetHookInRange(false);
            if (!isAttached && !hopperLift.IsCapturing)
            {
                hopperLiftingTarget = null;
            }
            inZone = false;
            return;
        }

        inZone = false;

        if (other.GetComponent<Rigidbody>() == rbTarget)
        {
            rbTarget = null;
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (Application.isPlaying)
            return;
        if (rbCurrent != null) {
            Gizmos.DrawWireSphere(new Vector3(rbCurrent.transform.position.x, posY, rbCurrent.transform.position.z), 0.05f);
        }
    }
#endif
}

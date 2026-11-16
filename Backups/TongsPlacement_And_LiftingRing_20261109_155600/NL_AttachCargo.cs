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
    public float connectPointOffset = 0.365f;
    private float posY;
    [HideInInspector] public bool isAttached = false;
    [Header("Hopper Lifting Target")]
    public LiquidSolidPrototype.Core.HopperCraneLiftingPoint hopperLiftingTarget;

    void Start()
    {
        //rbCurrent = GetComponent<Rigidbody>();

        if (Application.isPlaying)
        {
            trigger = gameObject.AddComponent<CapsuleCollider>();
            trigger.radius = 0.08f;
            trigger.height = 0.28f;
            trigger.center = new Vector3(0, -0.35f, 0);
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

            if (!isAttached && hopperLiftingTarget != null)
            {
                Vector3 hookTip = transform.position - Vector3.up * connectPointOffset;
                float dist = Vector3.Distance(hookTip, hopperLiftingTarget.CrossbarWorldPosition);
                if (dist <= 0.65f)
                {
                    inZone = true;
                    hopperLiftingTarget.SetHookInRange(true, transform, rbCurrent);
                }
                else if (hopperLiftingTarget.IsHookInRange)
                {
                    hopperLiftingTarget.SetHookInRange(false);
                    if (rbTarget == null)
                    {
                        inZone = false;
                    }
                }
            }

            // DesktopInteractionManager is the sole desktop input owner for ToggleAttach().
            // Redundant polling here is removed to prevent double-toggle in desktop crane mode.
            // VR or external integrations invoke ToggleAttach(), Attach(), or Detach() directly.
            /*
            if (Input.GetKeyDown(attachKey))
            {
                ToggleAttach();
            }
            */
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

        if (hopperLiftingTarget != null && hopperLiftingTarget.IsHookInRange)
        {
            isAttached = hopperLiftingTarget.AttachToHook(transform, rbCurrent);
            if (isAttached)
                return;
        }

        // Delegated attachment: HopperCraneLiftingPoint already created the FixedJoint.
        // No additional joint creation needed.
        // Mark attachment as successful and exit.
        return;
    }

    public void Detach()
    {
        if (!isAttached)
            return;

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

    /// <summary>
    /// Queries the attached payload for any minimum hook height constraint based on ground contact.
    /// Returns false and minHookHeight = 0 if unattached or no payload constraint exists.
    /// </summary>
    public bool TryGetPayloadMinHookHeight(float currentHookHeight, float craneHeight, float hookOffset, out float minHookHeight)
    {
        minHookHeight = 0f;
        if (!isAttached || hopperLiftingTarget == null)
            return false;

        return hopperLiftingTarget.TryGetMinAllowedHookHeight(currentHookHeight, craneHeight, hookOffset, out minHookHeight);
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

        var hopperLift = other.GetComponent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>() ?? other.GetComponentInParent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>();
        if (hopperLift != null)
        {
            hopperLiftingTarget = hopperLift;
            hopperLiftingTarget.SetHookInRange(true, transform, rbCurrent);
            inZone = true;
            return;
        }

        // Prevent hook from attaching to arbitrary hopper body, rim or liner surfaces
        if (other.name.Contains("Hopper") || (other.transform.root != null && other.transform.root.name.Contains("Hopper")))
        {
            return;
        }
        
        inZone = true;

        if (other.GetComponent<Rigidbody>())
        {
            rbTarget = other.GetComponent<Rigidbody>();
        }

        //print(other.name + " object inside the hook trigger. Ready to attach.");
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

        var hopperLift = other.GetComponent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>() ?? other.GetComponentInParent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>();
        if (hopperLift != null && hopperLift == hopperLiftingTarget)
        {
            hopperLiftingTarget.SetHookInRange(false);
            if (!isAttached)
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
        //print(other.name + " left the hook trigger.");
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

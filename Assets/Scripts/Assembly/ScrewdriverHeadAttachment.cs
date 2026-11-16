using System;
using UnityEngine;

/// <summary>
/// Manages the bit attachment socket / chuck on the screwdriver.
/// </summary>
public class ScrewdriverHeadAttachment : MonoBehaviour
{
    [SerializeField] private ScrewdriverHead currentHead;

    public ScrewdriverHead CurrentHead => currentHead;

    public event Action<ScrewdriverHead> HeadAttached;
    public event Action<ScrewdriverHead> HeadDetached;
    public event Action<ScrewdriverHead, string> HeadRejected;

    public void AttachHead(ScrewdriverHead head)
    {
        currentHead = head;
        HeadAttached?.Invoke(head);
    }

    public void DetachHead()
    {
        var prev = currentHead;
        currentHead = null;
        if (prev != null)
            HeadDetached?.Invoke(prev);
    }

    public void RejectHead(ScrewdriverHead head, string reason)
    {
        HeadRejected?.Invoke(head, reason);
    }
}

using UnityEngine;

/// <summary>
/// Represents an interchangeable screwdriver bit/head for fastener interaction.
/// </summary>
public class ScrewdriverHead : MonoBehaviour
{
    [SerializeField] private string displayName = "Bit";
    [SerializeField] private string fastenerCompatibilityId = "Universal";

    public string DisplayName => displayName;
    public string FastenerCompatibilityId => fastenerCompatibilityId;

    public void SetParameters(string name, string compatibilityId)
    {
        displayName = name;
        fastenerCompatibilityId = compatibilityId;
    }
}

using UnityEngine;
using UnityEngine.Animations.Rigging;

// Driven by the network controller's aim state on both local and remote players.
// The weapon stays attached to the animated right hand throughout equip/aim.
public class HandHeldWeaponIK : MonoBehaviour
{
    public Transform equippedWeapon;
    public Transform aimingPos;
    public Transform leftHandGrip;
    public TwoBoneIKConstraint rightHandIK;
    public TwoBoneIKConstraint leftHandIK;

    public void Apply(float weight)
    {
        weight = Mathf.Clamp01(weight);
        if (rightHandIK != null) rightHandIK.weight = 0f;
        if (leftHandIK != null) leftHandIK.weight = 0f;
        if (weight <= 0f || equippedWeapon == null || aimingPos == null || rightHandIK == null)
            return;

        var hand = rightHandIK.data.tip;
        var target = rightHandIK.data.target;
        // Never feed the solved hand/weapon world pose back into its own target.
        if (hand == null || target == null || aimingPos.IsChildOf(hand) || target.IsChildOf(hand) ||
            !TryGetLocalPose(hand, equippedWeapon, out var weaponInHand, out var weaponRotationInHand))
            return;

        var handRotation = aimingPos.rotation * Quaternion.Inverse(weaponRotationInHand);
        target.SetPositionAndRotation(
            aimingPos.position - handRotation * Vector3.Scale(weaponInHand, hand.lossyScale),
            handRotation);
        rightHandIK.weight = weight;

        if (leftHandIK != null && leftHandIK.data.target != null &&
            !leftHandIK.data.target.IsChildOf(hand) &&
            TryGetLocalPose(equippedWeapon, leftHandGrip, out var gripPosition, out var gripRotation))
        {
            // Predict the left grip from the desired weapon pose before the rig
            // evaluates, so the support hand does not lag a frame behind.
            leftHandIK.data.target.SetPositionAndRotation(
                aimingPos.position + aimingPos.rotation * Vector3.Scale(gripPosition, equippedWeapon.lossyScale),
                aimingPos.rotation * gripRotation);
            leftHandIK.weight = weight;
        }
    }

    private void OnDisable()
    {
        Apply(0f);
    }

    private static bool TryGetLocalPose(Transform ancestor, Transform child,
        out Vector3 position, out Quaternion rotation)
    {
        var matrix = Matrix4x4.identity;
        rotation = Quaternion.identity;
        var node = child;
        while (node != null && node != ancestor)
        {
            matrix = Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale) * matrix;
            rotation = node.localRotation * rotation;
            node = node.parent;
        }
        position = matrix.MultiplyPoint3x4(Vector3.zero);
        return child != null && node == ancestor;
    }
}

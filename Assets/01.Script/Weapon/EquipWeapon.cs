using UnityEngine;
using UnityEngine.Animations.Rigging;

public class EquipWeapon : MonoBehaviour
{
    [Header("Ray Settings")]
    [SerializeField][Range(0.0f, 2.0f)] private float rayLength;

    [SerializeField] private Vector3 rayOffset;
    [SerializeField] private LayerMask weaponMask;
    private RaycastHit topRayHitInfo;

    [SerializeField] private Transform currentWeaponPos;
    [SerializeField] private AttachedWeapon currentWeapon;

    [SerializeField] private Transform equipPos;
    [SerializeField] private Transform aimingPos;

    private bool isAiming = false;
    
    [Header("Right Hand Target")]
    [SerializeField] private TwoBoneIKConstraint rightHandIK; // 제약 조건

    [SerializeField] private Transform rightHandTarget; // 플레이어 손을 무기에 정확하게 배치하는데 사용할 타겟 참조

    [Header("Left Hand Target")]
    [SerializeField] private TwoBoneIKConstraint leftHnadIK;

    [SerializeField] private Transform leftHandTarget;

    [SerializeField] private Transform IKRightHandPos; // 플레이어 손을 무기에 정확하게 배치하는데 사용할 타겟의 위치
    [SerializeField] private Transform IKLeftHandPos;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.E))
        {
            Equip();
        }

        isAiming = Input.GetMouseButton(1);
        UpdateHandIK(isAiming);
    }

    private void UpdateHandIK(bool aiming)
    {
        rightHandIK.weight = 0;
        leftHnadIK.weight = 0;
        if (!aiming || currentWeapon == null || aimingPos == null)
            return;

        var hand = rightHandIK.data.tip;
        var weapon = currentWeapon.transform;
        // The aim control must be independent of the hand it drives.
        if (hand == null || aimingPos.IsChildOf(hand) ||
            !TryGetLocalPose(hand, weapon, out var weaponInHand, out var weaponRotationInHand))
            return;

        // Keep the weapon under WeaponHolder. Solve where its parent hand must
        // be for the weapon origin to coincide with the independent aim control.
        var handRotation = aimingPos.rotation * Quaternion.Inverse(weaponRotationInHand);
        var handPosition = aimingPos.position -
            handRotation * Vector3.Scale(weaponInHand, hand.lossyScale);
        rightHandTarget.SetPositionAndRotation(handPosition, handRotation);
        rightHandIK.weight = 1;

        if (TryGetLocalPose(weapon, IKLeftHandPos, out var leftGrip, out var leftRotation))
        {
            // Predict the grip from the desired weapon pose, avoiding a frame
            // of lag from reading the weapon before the right-hand IK solves.
            leftHandTarget.SetPositionAndRotation(
                aimingPos.position + aimingPos.rotation * Vector3.Scale(leftGrip, weapon.lossyScale),
                aimingPos.rotation * leftRotation);
            leftHnadIK.weight = 1;
        }
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

    private void FixedUpdate()
    {
        RaycastsHandler();
    }

    private void RaycastsHandler()
    {
        Ray topRay = new Ray(transform.position + rayOffset, transform.forward);
        
        Debug.DrawRay(transform.position + rayOffset , transform.forward * rayLength, Color.red);

        Physics.Raycast(topRay, out topRayHitInfo, rayLength, weaponMask);
    }

    private void Equip()
    {
        var holder = currentWeaponPos != null ? currentWeaponPos : equipPos;
        if (holder == null)
            return;

        if (topRayHitInfo.collider != null)
        {
            currentWeapon = topRayHitInfo.transform.gameObject.GetComponent<AttachedWeapon>();
            Debug.Log("Equip");
        }

        if (!currentWeapon) 
            return;

        currentWeapon.IsRotating = false;
        if (currentWeapon.TryGetComponent<Rigidbody>(out var weaponBody))
        {
            weaponBody.isKinematic = true;
            weaponBody.useGravity = false;
        }
        rightHandIK.weight = 0;
        currentWeapon.transform.SetParent(holder, true);
        currentWeapon.transform.localPosition = Vector3.zero;
        currentWeapon.transform.localRotation = Quaternion.identity;
    }
}

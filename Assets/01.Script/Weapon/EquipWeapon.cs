using System;
using Photon.Pun.Demo.Cockpit;
using UnityEngine;

public class EquipWeapon : MonoBehaviour
{
    [Header("Ray Settings")]
    [SerializeField][Range(0.0f, 2.0f)] private float rayLength;

    [SerializeField] private Vector3 rayOffset;
    [SerializeField] private LayerMask weaponMask;
    private RaycastHit topRayHitInfo;

    [SerializeField] private Transform currentWeaponPos;
    [SerializeField] private AttachedWeapon currentWeapon;
    
    private void Start()
    {

    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.E))
        {
            Equip();
        }
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

        currentWeapon.transform.SetParent(currentWeaponPos, false);
        currentWeapon.transform.localPosition = Vector3.zero;
        currentWeapon.transform.localRotation = Quaternion.identity;

    }
}

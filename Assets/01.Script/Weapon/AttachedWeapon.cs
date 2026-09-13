using System;
using ExitGames.Client.Photon;
using UnityEngine;

public class AttachedWeapon : MonoBehaviour
{
    private Rigidbody weaponBody;

    [SerializeField] private float rotationSpeed;

    public bool IsRotating { get; set; }

    private void Start()
    {
        weaponBody = GetComponent<Rigidbody>();

        if (weaponBody)
            weaponBody.isKinematic = false;

        IsRotating = true;
    }

    private void Update()
    {
        if(!IsRotating) return;
        transform.Rotate(Vector3.up, rotationSpeed * Time.deltaTime);
    }
}

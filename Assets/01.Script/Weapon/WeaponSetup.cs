using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeaponSetup : MonoBehaviour
{
    [SerializeField] private GameObject _ADSPoint;
    [SerializeField] private GameObject _FirePoint;
    private Weapon _weapon;

    private void Awake()
    {
        _weapon = GetComponentInParent<Weapon>();
    }

    private void OnEnable()
    {
        if (_weapon != null)
            Init();
    }
    public void Init()
    {
        _weapon._FirePoint = _FirePoint;
        _weapon._ADSPoint = _ADSPoint;
        _weapon._animator = GetComponent<Animator>();
    }
}

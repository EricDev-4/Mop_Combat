using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class ThirdPersonCombat : MonoBehaviour
{
    private Animator _animator;
    
    [Range(0, 1)]
    [SerializeField] private float layerWeight = 0;
    [Range(0, 10)] [SerializeField] private float adsSpeed = 5;

    private void Start()
    {
        _animator = GetComponentInChildren<Animator>();
    }

    private void Update()
    {
        if (_animator == null)
            return;

        float targetWeight = Input.GetMouseButton(1) ? 1f : 0f;
        layerWeight = Mathf.MoveTowards(
            layerWeight,
            targetWeight,
            adsSpeed * Time.deltaTime
        );
        
        _animator.SetLayerWeight(1, layerWeight);
    }
}

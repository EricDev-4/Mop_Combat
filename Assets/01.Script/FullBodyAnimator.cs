using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FullBodyAnimator : MonoBehaviour
{
    private Animator _animator;
    [Range(0,1)]
    [SerializeField] private float animWeight = 1;


    private void Start()
    {
        _animator = GetComponent<Animator>();
    }

    private void Update()
    {
        _animator.SetLayerWeight(1, animWeight);
    }
}
 
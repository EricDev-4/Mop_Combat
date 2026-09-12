using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

public class Movement : MonoBehaviour
{
    private static readonly int WalkingParameter = Animator.StringToHash("Walking");

    public float walkSpeed = 3f;
    public float sprintSpeed = 10f;
    public float maxVelocityChange = 10f;
    [Header("Jump")]
    public float jumpHeight = 8f;

    public float airControl = 0.5f;

    [Header("Head Bob")]
    [SerializeField] private bool headBobEnabled = true;
    [SerializeField, Min(0f)] private float headBobHeight = 0.025f;
    [SerializeField, Min(0f)] private float headBobWidth = 0.012f;
    [SerializeField, Min(0f)] private float headBobFrequency = 1.8f;
    [SerializeField, Min(0.01f)] private float headBobSmoothing = 10f;
    [SerializeField, Range(0f, 1f)] private float aimingBobMultiplier = 0.2f;

    private Transform headBobRoot;
    private Vector3 headBobOrigin;
    private float headBobPhase;
    private bool groundedForHeadBob;
    private Weapon _weapon;

    public void ConfigureHeadBob(Transform cameraRoot)
    {
        ResetHeadBob();
        headBobRoot = cameraRoot;
        if (headBobRoot == null)
            return;

        headBobOrigin = headBobRoot.localPosition;
        _weapon = headBobRoot.GetComponentInChildren<Weapon>(true);
    }

    private void LateUpdate()
    {
        if (headBobRoot == null || rb == null)
            return;

        Vector3 targetOffset = Vector3.zero;
        float horizontalSpeed = new Vector2(rb.velocity.x, rb.velocity.z).magnitude;
        bool movingOnGround = headBobEnabled && groundedForHeadBob &&
            input.sqrMagnitude > 0.01f && horizontalSpeed > 0.1f &&
            Mathf.Abs(rb.velocity.y) < 0.5f;

        if (movingOnGround)
        {
            float pace = Mathf.Clamp(horizontalSpeed / Mathf.Max(walkSpeed, 0.01f), 0.5f, 1.5f);
            headBobPhase = Mathf.Repeat(headBobPhase + Time.deltaTime * headBobFrequency *
                pace * Mathf.PI * 2f, Mathf.PI * 4f);
            float amplitude = _weapon != null && _weapon.isAiming
                ? aimingBobMultiplier : 1f;
            targetOffset = new Vector3(
                Mathf.Sin(headBobPhase * 0.5f) * headBobWidth,
                Mathf.Sin(headBobPhase) * headBobHeight, 0f) * amplitude;
        }
        else
        {
            headBobPhase = 0f;
        }

        float blend = 1f - Mathf.Exp(-headBobSmoothing * Time.deltaTime);
        headBobRoot.localPosition = Vector3.Lerp(
            headBobRoot.localPosition, headBobOrigin + targetOffset, blend);
    }

    private void OnDisable()
    {
        ResetHeadBob();
    }

    private void ResetHeadBob()
    {
        if (headBobRoot != null)
            headBobRoot.localPosition = headBobOrigin;
        headBobPhase = 0f;
        groundedForHeadBob = false;
    }
    
    private Vector2 input;
    private Rigidbody rb;

    private bool sprinting;
    private bool jumping;

    private bool grounded = false;
    private void Start()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void Update()
    {
        input = new Vector2(Input.GetAxisRaw("Horizontal"),  Input.GetAxisRaw("Vertical"));
        input.Normalize();
        
        sprinting = Input.GetButton("sprint");
        jumping = Input.GetButton("Jump");
    }
    private void OnTriggerStay(Collider other)
    {
        grounded = true;
    }
    private void FixedUpdate()
    {
        groundedForHeadBob = grounded && !jumping;
        if (grounded)
        {
            if (jumping)
            {
                rb.velocity = new Vector3(rb.velocity.x , jumpHeight , rb.velocity.z);
            }
            else if(input.magnitude > 0.5f)
            {
                rb.AddForce(CalculateMovement(sprinting? sprintSpeed : walkSpeed), ForceMode.VelocityChange);            
            }
            else
            {
                var velocity1 = rb.velocity;
                velocity1 = new Vector3(velocity1.x * 0.2f * Time.fixedDeltaTime, velocity1.y,
                    velocity1.z * 0.2f * Time.fixedDeltaTime);
                rb.velocity = velocity1;
            }
        }
        else
        {
            if(input.magnitude > 0.5f)
            {
                rb.AddForce(CalculateMovement(sprinting? sprintSpeed * airControl : walkSpeed* airControl), ForceMode.VelocityChange);            
            }
            else
            {
                var velocity1 = rb.velocity;
                velocity1 = new Vector3(velocity1.x * 0.2f * Time.fixedDeltaTime, velocity1.y,
                    velocity1.z * 0.2f * Time.fixedDeltaTime);
                rb.velocity = velocity1;
            }
        }


        bool walking = input.sqrMagnitude > 0.01f;
        _weapon._animator.SetBool(WalkingParameter, walking);

        grounded = false;
    }


    Vector3 CalculateMovement(float _speed)
    {
        Vector3 targetVelocity = new Vector3(input.x, 0, input.y);
        //객체의 로컬 공간 기준 방향 벡터를 월드 공간 기준 방향 벡터로 변환
        targetVelocity = transform.TransformDirection(targetVelocity);

        targetVelocity *= _speed;

        Vector3 velocity = rb.velocity;

        if (input.magnitude > 0.5f)
        {
            Vector3 velocityChage = targetVelocity - velocity;

            velocityChage.x = Mathf.Clamp(velocityChage.x, -maxVelocityChange, maxVelocityChange);
            velocityChage.z = Mathf.Clamp(velocityChage.z, -maxVelocityChange, maxVelocityChange);

            velocityChage.y = 0;

            return velocityChage;
        }
        else
            return new  Vector3();
    }
}

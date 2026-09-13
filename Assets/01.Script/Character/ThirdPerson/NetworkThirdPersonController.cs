using Photon.Pun;
using UnityEngine;

[DefaultExecutionOrder(-20)]
[RequireComponent(typeof(CharacterController), typeof(PhotonView))]
public class NetworkThirdPersonController : MonoBehaviourPun, IPunObservable
{
    public Animator animator;
    public Transform cameraTransform;
    public Weapon weapon;
    public UnityEngine.Animations.Rigging.TwoBoneIKConstraint leftHandIK;
    public HandHeldWeaponIK handIK;
    public float walkSpeed = 3f;
    public float sprintSpeed = 6f;
    public float jumpHeight = 1.4f;
    public float gravity = -20f;
    public float rotationSmoothTime = 0.08f;
    public float adsSpeed = 100f;
    [Tooltip("Rotate the character toward the camera while aiming. Disable to keep movement-direction rotation only.")]
    public bool faceCameraWhileAiming = false;

    public bool IsLocal { get; private set; }
    public float MoveSpeed { get; private set; }
    public bool Grounded { get; private set; }
    public bool Aiming { get; private set; }
    private CharacterController controller;
    private float verticalVelocity, rotationVelocity, aimWeight;
    private static readonly int SpeedHash = Animator.StringToHash("MoveSpeed");
    private static readonly int GroundedHash = Animator.StringToHash("Grounded");

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        if (animator != null) animator.applyRootMotion = false;
    }

    public void ConfigureOwnership(bool isLocal) { IsLocal = isLocal; }

    private void Update()
    {
        if (IsLocal) TickMovement();
        if (animator == null) 
            return;
        animator.SetFloat(SpeedHash, MoveSpeed, 0.1f, Time.deltaTime);
        animator.SetBool(GroundedHash, Grounded);
        aimWeight = Mathf.MoveTowards(aimWeight, Aiming ? 1f : 0f, adsSpeed * Time.deltaTime);
        if (animator.layerCount > 1)
            animator.SetLayerWeight(1, aimWeight);
        if (handIK != null)
            handIK.Apply(handIK.isActiveAndEnabled ? aimWeight : 0f);
        else if (leftHandIK != null)
            leftHandIK.weight = aimWeight;
    }

    private void TickMovement()
    {
        if (cameraTransform == null || !controller.enabled) return;
        bool acceptsInput = Cursor.lockState == CursorLockMode.Locked;
        Vector2 input = acceptsInput ? Vector2.ClampMagnitude(new Vector2(
            Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1f) : Vector2.zero;
        bool sprint = acceptsInput && (Input.GetButton("sprint") || Input.GetKey(KeyCode.LeftShift));
        Aiming = acceptsInput && (Input.GetMouseButton(1));
        Move(input, sprint, acceptsInput && Input.GetButtonDown("Jump"), Aiming, Time.deltaTime);
    }

    // Keep input sampling separate from the motor so the same movement step can be verified deterministically.
    private void Move(Vector2 input, bool sprint, bool jump, bool aiming, float deltaTime)
    {
        Vector3 cameraPosition = cameraTransform.position;
        Quaternion cameraRotation = cameraTransform.rotation;
        input = Vector2.ClampMagnitude(input, 1f);
        Aiming = aiming;
        Vector3 forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
        Vector3 right = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;
        Vector3 direction = (forward * input.y + right * input.x).normalized;
        Vector3 facing = Aiming && faceCameraWhileAiming ? forward : direction;
        if (facing.sqrMagnitude > 0.001f)
        {
            float angle = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, Mathf.SmoothDampAngle(transform.eulerAngles.y,
                angle, ref rotationVelocity, rotationSmoothTime, Mathf.Infinity, deltaTime), 0f);
        }
        if (controller.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f;
        if (controller.isGrounded && jump)
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        verticalVelocity += gravity * deltaTime;
        controller.Move((direction * input.magnitude * (sprint ? sprintSpeed : walkSpeed)
            + Vector3.up * verticalVelocity) * deltaTime);
        Grounded = controller.isGrounded;
        MoveSpeed = input.magnitude * (sprint ? 1f : 0.5f);
        // The camera stays under the network root for lifetime/ownership, but body
        // rotation must not rotate the aim ray before the orbit's LateUpdate.
        cameraTransform.SetPositionAndRotation(cameraPosition, cameraRotation);
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            stream.SendNext(MoveSpeed);
            stream.SendNext(Grounded);
            stream.SendNext(Aiming);
        }
        else
        {
            MoveSpeed = (float)stream.ReceiveNext();
            Grounded = (bool)stream.ReceiveNext();
            Aiming = (bool)stream.ReceiveNext();
        }
    }
}

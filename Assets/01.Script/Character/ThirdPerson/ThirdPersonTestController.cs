using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class ThirdPersonTestController : MonoBehaviour
{
    private static readonly int MoveSpeedHash = Animator.StringToHash("MoveSpeed");
    private static readonly int GroundedHash = Animator.StringToHash("Grounded");

    [Header("References")]
    [SerializeField] private Animator animator;
    [SerializeField] private Transform cameraTransform;

    [Header("Movement")]
    [SerializeField, Min(0f)] private float walkSpeed = 3f;
    [SerializeField, Min(0f)] private float sprintSpeed = 6f;
    [SerializeField, Min(0f)] private float rotationSmoothTime = 0.08f;

    [Header("Jump")]
    [SerializeField, Min(0f)] private float jumpHeight = 1.4f;
    [SerializeField] private float gravity = -20f;

    private CharacterController characterController;
    private float verticalVelocity;
    private float rotationVelocity;

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (animator != null)
            animator.applyRootMotion = false;
    }

    private void Update()
    {
        bool grounded = characterController.isGrounded;
        if (grounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        Vector2 input = new Vector2(
            Input.GetAxisRaw("Horizontal"),
            Input.GetAxisRaw("Vertical"));
        input = Vector2.ClampMagnitude(input, 1f);

        bool sprinting = Input.GetButton("sprint") ||
                         Input.GetKey(KeyCode.LeftShift);
        float currentSpeed = sprinting ? sprintSpeed : walkSpeed;

        Vector3 moveDirection = GetCameraRelativeDirection(input);
        if (moveDirection.sqrMagnitude > 0.001f)
        {
            float targetAngle = Mathf.Atan2(moveDirection.x, moveDirection.z) *
                                Mathf.Rad2Deg;
            float smoothedAngle = Mathf.SmoothDampAngle(
                transform.eulerAngles.y,
                targetAngle,
                ref rotationVelocity,
                rotationSmoothTime);

            transform.rotation = Quaternion.Euler(0f, smoothedAngle, 0f);
            characterController.Move(moveDirection * currentSpeed * Time.deltaTime);
        }

        if (grounded && Input.GetButtonDown("Jump"))
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;
        characterController.Move(Vector3.up * verticalVelocity * Time.deltaTime);

        if (animator != null)
        {
            float normalizedSpeed = input.magnitude > 0.01f
                ? (sprinting ? 1f : 0.5f)
                : 0f;

            animator.SetFloat(MoveSpeedHash, normalizedSpeed, 0.1f, Time.deltaTime);
            animator.SetBool(GroundedHash, characterController.isGrounded);
        }
    }

    private Vector3 GetCameraRelativeDirection(Vector2 input)
    {
        Vector3 forward = cameraTransform != null
            ? cameraTransform.forward
            : Vector3.forward;
        Vector3 right = cameraTransform != null
            ? cameraTransform.right
            : Vector3.right;

        forward.y = 0f;
        right.y = 0f;
        forward.Normalize();
        right.Normalize();

        return (forward * input.y + right * input.x).normalized;
    }

    private void OnGUI()
    {
        GUI.Box(new Rect(16f, 16f, 280f, 92f), "Full Body / IK Test");
        GUI.Label(new Rect(32f, 44f, 250f, 20f), "WASD: Move   Shift: Sprint");
        GUI.Label(new Rect(32f, 64f, 250f, 20f), "Space: Jump   Mouse: Orbit");
        GUI.Label(new Rect(32f, 84f, 250f, 20f), "Esc: Toggle cursor lock");
    }
}

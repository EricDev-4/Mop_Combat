using UnityEngine;

public class ThirdPersonOrbitCamera : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField, Min(0.1f)] private float distance = 5f;
    [SerializeField] private float targetHeight = 1.5f;
    [SerializeField, Min(0f)] private float mouseSensitivity = 3f;
    [SerializeField, Min(0f)] private float positionSmoothing = 15f;
    [SerializeField] private Vector2 pitchLimits = new Vector2(-25f, 70f);
    [SerializeField] private LayerMask collisionMask = Physics.DefaultRaycastLayers;
    [SerializeField, Min(0.01f)] private float collisionRadius = 0.25f;
    [SerializeField, Min(0.01f)] private float collisionPadding = 0.05f;

    private Camera orbitCamera;

    private float yaw;
    private float pitch = 15f;
    private bool cursorLocked = true;

    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
    }

    private void Start()
    {
        orbitCamera = GetComponent<Camera>();
        if (target != null)
            yaw = target.eulerAngles.y;

        ApplyCursorState();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            cursorLocked = !cursorLocked;
            ApplyCursorState();
        }

        if (!cursorLocked)
            return;

        yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
        pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
        pitch = Mathf.Clamp(pitch, pitchLimits.x, pitchLimits.y);
    }

    private void LateUpdate()
    {
        if (target == null)
            return;

        Vector3 pivot = target.position + Vector3.up * targetHeight; // 캐릭터 기준점
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 desiredPosition = ResolveCollision(pivot, pivot + rotation * Vector3.back * distance);

        float blend = 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime);
        // Smoothing can cut through the floor even when its destination is safe.
        Vector3 smoothedPosition = Vector3.Lerp(transform.position, desiredPosition, blend);
        transform.position = ResolveCollision(pivot, smoothedPosition);
        transform.rotation = rotation;
    }

    private Vector3 ResolveCollision(Vector3 pivot, Vector3 position)
    {
        Vector3 offset = position - pivot;
        float length = offset.magnitude;
        if (length < 0.0001f)
            return pivot;

        float radius = collisionRadius;
        if (orbitCamera != null)
        {
            // Protect the near-plane corners as well as the camera's center.
            float halfHeight = orbitCamera.orthographic
                ? orbitCamera.orthographicSize
                : orbitCamera.nearClipPlane * Mathf.Tan(orbitCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfWidth = halfHeight * orbitCamera.aspect;
            radius = Mathf.Max(radius, Mathf.Sqrt(halfHeight * halfHeight + halfWidth * halfWidth
                + orbitCamera.nearClipPlane * orbitCamera.nearClipPlane));
        }

        Vector3 direction = offset / length;
        float safeLength = length;
        RaycastHit[] hits = Physics.SphereCastAll(pivot, radius, direction, length,
            collisionMask, QueryTriggerInteraction.Ignore);
        foreach (RaycastHit hit in hits)
        {
            // The player's own controller/equipment must not pull the camera in.
            if (hit.collider.transform == target || hit.collider.transform.IsChildOf(target)
                || hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform))
                continue;

            safeLength = Mathf.Min(safeLength, Mathf.Max(0f, hit.distance - collisionPadding));
        }

        return pivot + direction * safeLength;
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus)
            ApplyCursorState();
    }

    private void ApplyCursorState()
    {
        Cursor.lockState = cursorLocked
            ? CursorLockMode.Locked
            : CursorLockMode.None;
        Cursor.visible = !cursorLocked;
    }
}

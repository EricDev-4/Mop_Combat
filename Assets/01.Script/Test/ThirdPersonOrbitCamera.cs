using UnityEngine;

public class ThirdPersonOrbitCamera : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField, Min(0.1f)] private float distance = 5f;
    [SerializeField] private float targetHeight = 1.5f;
    [SerializeField, Min(0f)] private float mouseSensitivity = 3f;
    [SerializeField, Min(0f)] private float positionSmoothing = 15f;
    [SerializeField] private Vector2 pitchLimits = new Vector2(-25f, 70f);

    private float yaw;
    private float pitch = 15f;
    private bool cursorLocked = true;

    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
    }

    private void Start()
    {
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
        Vector3 desiredPosition = pivot + rotation * Vector3.back * distance;

        float blend = 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, desiredPosition, blend);
        transform.rotation = rotation;
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

using UnityEngine;

public class WeaponSway : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform swayPivot;

    [Header("Settings")] 
    [SerializeField, Min(0f)] private float swayClamp = 0.09f;
    [SerializeField, Min(0f)] private float smoothing = 3f;

    private Vector3 origin;

    private void Awake()
    {
        if (swayPivot == null)
        {
            Debug.LogError("WeaponSway: Sway Pivot is not assigned.", this);
            enabled = false;
            return;
        }

        origin = swayPivot.localPosition;
    }

    private void LateUpdate()
    {
        Vector2 input = new Vector2(Input.GetAxisRaw("Mouse X") , Input.GetAxisRaw("Mouse Y"));
        
        input.x = Mathf.Clamp(input.x, -swayClamp, swayClamp);
        input.y = Mathf.Clamp(input.y, -swayClamp, swayClamp);
        
        Vector3 target = new Vector3(-input.x, -input.y, 0f);
        float lerpFactor = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
        
        //“원래 위치를 기준으로 target만큼 흔들겠다”
        swayPivot.localPosition = Vector3.Lerp(swayPivot.localPosition, target + origin, lerpFactor);
    }
}

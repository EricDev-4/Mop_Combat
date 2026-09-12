using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FaceObjectToCamera : MonoBehaviour
{
    private Camera cam;
    void LateUpdate()
    {
        if (cam == null)
            cam = Camera.main;
        
        if(cam == null)
            return;
        // nameTag transform 이 항상 카메라를 향하도록
        transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, cam.transform.up);
    }
}

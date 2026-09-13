using System;
using System.Reflection;
using UnityEngine;

public static class VerifyCameraCollision
{
    public static string Main()
    {
        var orbit = UnityEngine.Object.FindAnyObjectByType<ThirdPersonOrbitCamera>();
        if (orbit == null) throw new Exception("Orbit camera missing");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = orbit.GetType();
        var target = (Transform)type.GetField("target", flags).GetValue(orbit);
        var height = (float)type.GetField("targetHeight", flags).GetValue(orbit);
        var distance = (float)type.GetField("distance", flags).GetValue(orbit);
        var cameraField = type.GetField("orbitCamera", flags);
        var previousCamera = cameraField.GetValue(orbit);
        var camera = orbit.GetComponent<Camera>();
        var resolve = type.GetMethod("ResolveCollision", flags);
        var pivot = target.position + Vector3.up * height;
        var ground = GameObject.Find("Ground").GetComponent<Collider>();
        if (!ground.enabled || ground.isTrigger) throw new Exception("Ground collider unavailable");
        int cases = 0, previouslyBelowGround = 0;
        float lowestY = float.PositiveInfinity;
        try
        {
            cameraField.SetValue(orbit, camera);
            foreach (float pitch in new float[] { -25f, -15f, 0f, 15f, 45f, 70f })
            for (int yaw = 0; yaw < 360; yaw += 10)
            {
                var rotation = Quaternion.Euler(pitch, yaw, 0f);
                var desired = pivot + rotation * Vector3.back * distance;
                if (desired.y < ground.bounds.max.y) previouslyBelowGround++;
                var safe = (Vector3)resolve.Invoke(orbit, new object[] { pivot, desired });
                var smoothed = Vector3.Lerp(desired, safe, 0.1f);
                safe = (Vector3)resolve.Invoke(orbit, new object[] { pivot, smoothed });
                if (safe.y <= ground.bounds.max.y) throw new Exception("Camera below ground at pitch " + pitch);
                float hh = camera.nearClipPlane * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
                float hw = hh * camera.aspect;
                foreach (float x in new float[] { -hw, hw })
                foreach (float y in new float[] { -hh, hh })
                {
                    var corner = safe + rotation * new Vector3(x, y, camera.nearClipPlane);
                    if (corner.y <= ground.bounds.max.y) throw new Exception("Near plane below ground");
                }
                lowestY = Mathf.Min(lowestY, safe.y);
                cases++;
            }
        }
        finally { cameraField.SetValue(orbit, previousCamera); }
        return "PASS: " + cases + " pitch/yaw + smoothing cases; original below-floor positions="
            + previouslyBelowGround + "; minimum corrected Y=" + lowestY + "; ground Y=" + ground.bounds.max.y;
    }
}

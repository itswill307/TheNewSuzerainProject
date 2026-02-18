using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public class DirectionalLightFollowCamera : MonoBehaviour
{
    [Header("References")]
    [SerializeField] Camera targetCamera;
    [SerializeField] bool useMainCameraIfMissing = true;

    [Header("Behavior")]
    [SerializeField] bool lookOppositeCameraForward = false;
    [SerializeField] Vector3 eulerOffset = Vector3.zero;
    [SerializeField] bool updateInEditMode = false;

    Light attachedLight;
    bool warnedNonDirectional;

    void Reset()
    {
        attachedLight = GetComponent<Light>();
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }
    }

    void OnEnable()
    {
        attachedLight = GetComponent<Light>();
        TryAutoAssignCamera();
        warnedNonDirectional = false;
    }

    void OnValidate()
    {
        attachedLight = GetComponent<Light>();
        TryAutoAssignCamera();
    }

    void LateUpdate()
    {
        if (!Application.isPlaying && !updateInEditMode)
        {
            return;
        }

        ApplyRotation();
    }

    void TryAutoAssignCamera()
    {
        if (targetCamera != null || !useMainCameraIfMissing)
        {
            return;
        }

        targetCamera = Camera.main;
    }

    void ApplyRotation()
    {
        Camera cam = targetCamera;
        if (cam == null && useMainCameraIfMissing)
        {
            cam = Camera.main;
        }
        if (cam == null)
        {
            return;
        }

        if (attachedLight != null && attachedLight.type != LightType.Directional && !warnedNonDirectional)
        {
            Debug.LogWarning("DirectionalLightFollowCamera is intended for Directional Lights.", this);
            warnedNonDirectional = true;
        }

        Quaternion rot = cam.transform.rotation;
        if (lookOppositeCameraForward)
        {
            rot = rot * Quaternion.Euler(0f, 180f, 0f);
        }

        transform.rotation = rot * Quaternion.Euler(eulerOffset);
    }
}

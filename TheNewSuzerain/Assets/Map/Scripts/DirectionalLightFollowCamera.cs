using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public class DirectionalLightFollowCamera : MonoBehaviour
{
    [Header("References")]
    [SerializeField] MapControllerEqr mapController;
    [SerializeField] Camera targetCamera;
    [SerializeField] bool useMainCameraIfMissing = true;

    [Header("Behavior")]
    [SerializeField] bool useMapControllerBaseRotation = true;
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
        if (mapController == null && targetCamera != null)
        {
            mapController = targetCamera.GetComponent<MapControllerEqr>();
        }
    }

    void OnEnable()
    {
        attachedLight = GetComponent<Light>();
        TryAutoAssignCamera();
        TryAutoAssignMapController();
        warnedNonDirectional = false;
    }

    void OnValidate()
    {
        attachedLight = GetComponent<Light>();
        TryAutoAssignCamera();
        TryAutoAssignMapController();
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

    void TryAutoAssignMapController()
    {
        if (mapController != null)
        {
            return;
        }

        if (targetCamera != null)
        {
            mapController = targetCamera.GetComponent<MapControllerEqr>();
        }
        else if (useMainCameraIfMissing && Camera.main != null)
        {
            mapController = Camera.main.GetComponent<MapControllerEqr>();
        }
    }

    void ApplyRotation()
    {
        Quaternion sourceRotation;
        if (!TryGetSourceRotation(out sourceRotation))
        {
            return;
        }

        if (attachedLight != null && attachedLight.type != LightType.Directional && !warnedNonDirectional)
        {
            Debug.LogWarning("DirectionalLightFollowCamera is intended for Directional Lights.", this);
            warnedNonDirectional = true;
        }

        Quaternion rot = sourceRotation;
        if (lookOppositeCameraForward)
        {
            rot = rot * Quaternion.Euler(0f, 180f, 0f);
        }

        transform.rotation = rot * Quaternion.Euler(eulerOffset);
    }

    bool TryGetSourceRotation(out Quaternion sourceRotation)
    {
        sourceRotation = Quaternion.identity;

        if (useMapControllerBaseRotation)
        {
            TryAutoAssignMapController();
            if (mapController != null)
            {
                sourceRotation = mapController.GetBaseCameraLookRotation();
                return true;
            }
        }

        Camera cam = targetCamera;
        if (cam == null && useMainCameraIfMissing)
        {
            cam = Camera.main;
        }
        if (cam == null)
        {
            return false;
        }

        sourceRotation = cam.transform.rotation;
        return true;
    }
}

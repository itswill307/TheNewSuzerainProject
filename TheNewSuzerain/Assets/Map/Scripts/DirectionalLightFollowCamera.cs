using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public class DirectionalLightFollowCamera : MonoBehaviour
{
    [SerializeField] MapCesiumTransitionManager transitionManager;
    [SerializeField] CesiumMapController cesiumController;

    [Header("Plane")]
    [SerializeField] Transform planeTransform;
    [SerializeField] Vector3 localPlaneNormal = Vector3.back;
    [SerializeField] Vector3 localPlaneUp = Vector3.up;
    [SerializeField] bool pointTowardPlane = true;

    [Header("Behavior")]
    [SerializeField] Vector3 eulerOffset = Vector3.zero;
    [SerializeField] bool updateInEditMode = false;

    Light attachedLight;
    bool warnedNonDirectional;

    void Reset()
    {
        attachedLight = GetComponent<Light>();
    }

    void OnEnable()
    {
        attachedLight = GetComponent<Light>();
        warnedNonDirectional = false;
        ResolveReferences();
    }

    void OnValidate()
    {
        attachedLight = GetComponent<Light>();
        ResolveReferences();
        if (localPlaneNormal.sqrMagnitude < 1e-6f)
        {
            localPlaneNormal = Vector3.back;
        }

        if (localPlaneUp.sqrMagnitude < 1e-6f)
        {
            localPlaneUp = Vector3.up;
        }
    }

    void LateUpdate()
    {
        if (!Application.isPlaying && !updateInEditMode)
        {
            return;
        }

        ApplyRotation();
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

        transform.rotation = sourceRotation * Quaternion.Euler(eulerOffset);
    }

    bool TryGetSourceRotation(out Quaternion sourceRotation)
    {
        ResolveReferences();
        if (IsCesiumModeActive() &&
            cesiumController != null)
        {
            sourceRotation = cesiumController.GetBaseCameraLookRotation();
            return true;
        }

        Vector3 planeNormal = localPlaneNormal.normalized;
        Vector3 planeUp = localPlaneUp.normalized;
        if (planeTransform != null)
        {
            planeNormal = planeTransform.TransformDirection(planeNormal).normalized;
            planeUp = planeTransform.TransformDirection(planeUp).normalized;
        }

        planeUp = Vector3.ProjectOnPlane(planeUp, planeNormal).normalized;
        if (planeUp.sqrMagnitude < 1e-6f)
        {
            planeUp = Mathf.Abs(Vector3.Dot(planeNormal, Vector3.up)) < 0.99f
                ? Vector3.ProjectOnPlane(Vector3.up, planeNormal).normalized
                : Vector3.ProjectOnPlane(Vector3.right, planeNormal).normalized;
        }

        Vector3 forward = pointTowardPlane ? -planeNormal : planeNormal;
        sourceRotation = Quaternion.LookRotation(forward, planeUp);
        return true;
    }

    void ResolveReferences()
    {
        if (transitionManager == null)
        {
            transitionManager = FindFirstObjectByType<MapCesiumTransitionManager>(FindObjectsInactive.Include);
        }

        if (cesiumController == null)
        {
            cesiumController = FindFirstObjectByType<CesiumMapController>(FindObjectsInactive.Include);
        }
    }

    bool IsCesiumModeActive()
    {
        if (transitionManager != null)
        {
            return transitionManager.ActiveMode == MapCesiumTransitionManager.ViewMode.Cesium;
        }

        return cesiumController != null && cesiumController.isActiveAndEnabled;
    }
}

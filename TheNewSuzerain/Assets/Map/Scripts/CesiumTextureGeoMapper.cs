using System.Collections.Generic;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Binds Cesium raster overlay UVs to global geographic UVs for stable landcover detail texture sampling.
/// </summary>
public class CesiumTextureGeoMapper : MonoBehaviour
{
    [SerializeField] CesiumGeoreference georeference;
    [SerializeField] Cesium3DTileset cesiumTileset;
    [SerializeField, Min(0.02f)] float updateIntervalSeconds = 0.2f;

    CesiumEllipsoid ellipsoid;
    float nextUpdateTime;

    readonly List<Vector2> overlayUvBuffer = new List<Vector2>(4096);
    readonly List<float> overlayFitU = new List<float>(4096);
    readonly List<float> overlayFitV = new List<float>(4096);
    readonly List<float> geoFitU = new List<float>(4096);
    readonly List<float> geoFitV = new List<float>(4096);

    static readonly int OverlayTextureCoordinateIndexProp = Shader.PropertyToID("_overlayTextureCoordinateIndex_0");
    static readonly int OverlayTranslationAndScaleProp = Shader.PropertyToID("_overlayTranslationAndScale_0");
    static readonly int OverlayGeoUvScaleOffsetProp = Shader.PropertyToID("_OverlayGeoUvScaleOffset_0");
    static readonly int OverlayGeoRectValidProp = Shader.PropertyToID("_OverlayGeoRectValid_0");
    static readonly int FlipOverlayVProp = Shader.PropertyToID("_FlipOverlayV");

    void Awake()
    {
        ResolveReferences();
    }

    void OnEnable()
    {
        ResolveReferences();
        nextUpdateTime = 0f;
    }

    void Update()
    {
        if (Time.unscaledTime < nextUpdateTime)
            return;

        nextUpdateTime = Time.unscaledTime + updateIntervalSeconds;
        UpdateOverlayGeoUvScaleOffsets();
    }

    void ResolveReferences()
    {
        if (georeference == null)
            georeference = GetComponentInParent<CesiumGeoreference>();

        if (cesiumTileset == null)
            cesiumTileset = GetComponentInChildren<Cesium3DTileset>(true);

        if (cesiumTileset == null)
            cesiumTileset = FindFirstObjectByType<Cesium3DTileset>(FindObjectsInactive.Include);

        if (georeference != null)
        {
            georeference.Initialize();
            ellipsoid = georeference.ellipsoid;
        }
    }

    void UpdateOverlayGeoUvScaleOffsets()
    {
        if (cesiumTileset == null || georeference == null || ellipsoid == null)
        {
            ResolveReferences();
            if (cesiumTileset == null || georeference == null || ellipsoid == null)
                return;
        }

        MeshRenderer[] renderers = cesiumTileset.GetComponentsInChildren<MeshRenderer>(false);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            MeshRenderer meshRenderer = renderers[rendererIndex];
            if (meshRenderer == null)
                continue;

            MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
                continue;

            Material[] materials = meshRenderer.sharedMaterials;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                Material material = materials[materialIndex];
                if (material == null ||
                    !material.HasProperty(OverlayGeoUvScaleOffsetProp) ||
                    !material.HasProperty(OverlayGeoRectValidProp) ||
                    !material.HasProperty(OverlayTranslationAndScaleProp) ||
                    !material.HasProperty(OverlayTextureCoordinateIndexProp))
                {
                    continue;
                }

                if (TryComputeOverlayGeoUvScaleOffset(meshFilter, material, out Vector4 scaleOffset))
                {
                    material.SetVector(OverlayGeoUvScaleOffsetProp, scaleOffset);
                    material.SetFloat(OverlayGeoRectValidProp, 1f);
                }
                else
                {
                    material.SetFloat(OverlayGeoRectValidProp, 0f);
                }
            }
        }
    }

    bool TryComputeOverlayGeoUvScaleOffset(MeshFilter meshFilter, Material material, out Vector4 scaleOffset)
    {
        scaleOffset = new Vector4(1f, 1f, 0f, 0f);

        Mesh mesh = meshFilter.sharedMesh;
        if (mesh == null || mesh.vertexCount <= 0)
            return false;

        int uvChannel = Mathf.Clamp(Mathf.RoundToInt(material.GetFloat(OverlayTextureCoordinateIndexProp)), 0, 3);
        overlayUvBuffer.Clear();
        mesh.GetUVs(uvChannel, overlayUvBuffer);
        if (overlayUvBuffer.Count != mesh.vertexCount)
            return false;

        Vector3[] vertices = mesh.vertices;
        if (vertices == null || vertices.Length != mesh.vertexCount)
            return false;

        Vector4 overlayTranslationAndScale = material.GetVector(OverlayTranslationAndScaleProp);
        bool flipOverlayV = material.HasProperty(FlipOverlayVProp) && material.GetFloat(FlipOverlayVProp) > 0.5f;

        overlayFitU.Clear();
        overlayFitV.Clear();
        geoFitU.Clear();
        geoFitV.Clear();

        Transform meshTransform = meshFilter.transform;
        Transform geoTransform = georeference.transform;

        float minGeoU = float.PositiveInfinity;
        float maxGeoU = float.NegativeInfinity;

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector2 uv = overlayUvBuffer[i];
            Vector2 overlayUv = new Vector2(
                uv.x * overlayTranslationAndScale.z + overlayTranslationAndScale.x,
                uv.y * overlayTranslationAndScale.w + overlayTranslationAndScale.y);

            if (flipOverlayV)
                overlayUv.y = 1f - overlayUv.y;

            Vector3 worldPosition = meshTransform.TransformPoint(vertices[i]);
            Vector3 localPosition = geoTransform.InverseTransformPoint(worldPosition);
            double3 ecef = georeference.TransformUnityPositionToEarthCenteredEarthFixed(
                new double3(localPosition.x, localPosition.y, localPosition.z));
            double3 llh = ellipsoid.CenteredFixedToLongitudeLatitudeHeight(ecef);

            float geoU = Mathf.Repeat((float)(llh.x / 360.0 + 0.5), 1f);
            float geoV = Mathf.Clamp01((float)(llh.y / 180.0 + 0.5));

            overlayFitU.Add(overlayUv.x);
            overlayFitV.Add(overlayUv.y);
            geoFitU.Add(geoU);
            geoFitV.Add(geoV);

            minGeoU = Mathf.Min(minGeoU, geoU);
            maxGeoU = Mathf.Max(maxGeoU, geoU);
        }

        if (maxGeoU - minGeoU > 0.5f)
        {
            for (int i = 0; i < geoFitU.Count; i++)
            {
                if (geoFitU[i] < 0.5f)
                    geoFitU[i] += 1f;
            }
        }

        if (!TryFitLinear(overlayFitU, geoFitU, out float scaleX, out float offsetX) ||
            !TryFitLinear(overlayFitV, geoFitV, out float scaleY, out float offsetY))
        {
            return false;
        }

        scaleOffset = new Vector4(scaleX, scaleY, offsetX, offsetY);
        return true;
    }

    static bool TryFitLinear(List<float> xValues, List<float> yValues, out float scale, out float offset)
    {
        scale = 1f;
        offset = 0f;

        int count = Mathf.Min(xValues.Count, yValues.Count);
        if (count < 2)
            return false;

        double sumX = 0.0;
        double sumY = 0.0;
        for (int i = 0; i < count; i++)
        {
            sumX += xValues[i];
            sumY += yValues[i];
        }

        double meanX = sumX / count;
        double meanY = sumY / count;
        double varianceX = 0.0;
        double covariance = 0.0;

        for (int i = 0; i < count; i++)
        {
            double dx = xValues[i] - meanX;
            double dy = yValues[i] - meanY;
            varianceX += dx * dx;
            covariance += dx * dy;
        }

        if (varianceX <= 1e-12)
            return false;

        double fittedScale = covariance / varianceX;
        double fittedOffset = meanY - fittedScale * meanX;
        if (double.IsNaN(fittedScale) || double.IsInfinity(fittedScale) ||
            double.IsNaN(fittedOffset) || double.IsInfinity(fittedOffset))
        {
            return false;
        }

        scale = (float)fittedScale;
        offset = (float)fittedOffset;
        return true;
    }

}

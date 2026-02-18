using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Renderer))]
public class ProvincePickerEqr : MonoBehaviour
{
    [Header("Refs")]
    public Camera cam;                 // your WorldMapController camera
    public Material mapMaterial;       // same material you set _Morph/_UVOffset on
    public Texture2D provinceIdTex;    // readable, point, no mips
    [SerializeField] Renderer[] additionalMapRenderers; // e.g., local detail patch renderer(s)
    [SerializeField] Material[] additionalMapMaterials;

    [Header("Highlight (optional)")]
    public bool highlightHovered = true;
    public string selectedIdProp   = "_SelectedID";
    public string highlightColorProp= "_HighlightColor";
    public Color highlightColor    = new Color(1, 0.75f, 0f, 0.6f); // A = strength
    public string hoverIdProp      = "_HoverID";
    public string hoverColorProp   = "_HoverColor";
    public Color hoverColor        = new Color(0f, 1f, 1f, 0.5f);

    [Header("Masking")]
    [SerializeField] bool blockOcean = true;
    [SerializeField] int oceanId = 0; // Treat this ID as unhoverable/unselectable (background/ocean)

    Renderer rend;
    readonly System.Collections.Generic.List<Renderer> targetRenderers = new System.Collections.Generic.List<Renderer>(8);
    readonly System.Collections.Generic.List<Material> targetMaterials = new System.Collections.Generic.List<Material>(8);
    Color32[] idPixels;
    int texW, texH;
    InputSystem_Actions input;

    void Awake()
    {
        if (!cam) cam = Camera.main;
        rend = GetComponent<Renderer>();
        input = new InputSystem_Actions();

        if (!provinceIdTex || !provinceIdTex.isReadable)
        {
            Debug.LogError("Province ID texture must be assigned and Read/Write enabled.");
            enabled = false; return;
        }
        texW = provinceIdTex.width;
        texH = provinceIdTex.height;
        idPixels = provinceIdTex.GetPixels32(); // cache for speed

        // Ensure material has the Province ID texture bound
        CollectTargetMaterials();
        ApplyProvinceIdTextureToAll();
        SetIntOnAll(selectedIdProp, -1);
        SetIntOnAll(hoverIdProp, -1);
    }

    void OnEnable() => input.Enable();
    void OnDisable() => input.Disable();

    void Update()
    {
        if (!cam || !mapMaterial) return;
        CollectTargetMaterials();
        ApplyProvinceIdTextureToAll();

        if (TryGetUVUnderCursor(out Vector2 uv))
        {
            float sphereFlag = mapMaterial.GetFloat("_Sphere");

            // Apply the same sampling offset as your shader (horizontal repeat)
            Vector2 uvOffset = mapMaterial.GetVector("_UVOffset"); // focusLon/360
            float u = uv.x;
            float v = uv.y;
            if (sphereFlag <= 0.5f)
            {
                u += uvOffset.x;
                v += uvOffset.y; // you keep Y = 0; still safe
            }
            u = u - Mathf.Floor(u);      // repeat X
            v = Mathf.Clamp01(v);        // clamp Y

            int pid = SampleProvinceId(u, v);

            // Block ocean/background selection/hover
            if (blockOcean && pid == oceanId)
            {
                SetIntOnAll(hoverIdProp, -1);
                // Ignore clicks on ocean
                return;
            }

            if (highlightHovered)
            {
                SetIntOnAll(hoverIdProp, pid);               // live hover id
                SetColorOnAll(hoverColorProp, hoverColor);   // hover color
            }

            if (input.Map.LMB.WasPressedThisFrame())
            {
                SetIntOnAll(selectedIdProp, pid);               // commit selection
                SetColorOnAll(highlightColorProp, highlightColor);
                Debug.Log($"Clicked province ID = {pid}");
            }
        }
        else
        {
            // No hover
            SetIntOnAll(hoverIdProp, -1);
        }
    }

    void CollectTargetMaterials()
    {
        targetMaterials.Clear();
        AddMaterialIfValid(mapMaterial);

        if (additionalMapMaterials != null)
        {
            for (int i = 0; i < additionalMapMaterials.Length; i++)
            {
                AddMaterialIfValid(additionalMapMaterials[i]);
            }
        }

        if (additionalMapRenderers != null)
        {
            for (int i = 0; i < additionalMapRenderers.Length; i++)
            {
                Renderer candidate = additionalMapRenderers[i];
                if (!candidate) continue;
                Material shared = candidate.sharedMaterial;
                if (shared) AddMaterialIfValid(shared);
            }
        }
    }

    void AddMaterialIfValid(Material mat)
    {
        if (!mat) return;
        if (!targetMaterials.Contains(mat))
        {
            targetMaterials.Add(mat);
        }
    }

    void ApplyProvinceIdTextureToAll()
    {
        if (!provinceIdTex) return;
        for (int i = 0; i < targetMaterials.Count; i++)
        {
            Material mat = targetMaterials[i];
            if (!mat || !mat.HasProperty("_ProvinceIDTex")) continue;
            mat.SetTexture("_ProvinceIDTex", provinceIdTex);
        }
    }

    void SetIntOnAll(string prop, int value)
    {
        for (int i = 0; i < targetMaterials.Count; i++)
        {
            Material mat = targetMaterials[i];
            if (!mat || !mat.HasProperty(prop)) continue;
            mat.SetInt(prop, value);
        }
    }

    void SetColorOnAll(string prop, Color value)
    {
        for (int i = 0; i < targetMaterials.Count; i++)
        {
            Material mat = targetMaterials[i];
            if (!mat || !mat.HasProperty(prop)) continue;
            mat.SetColor(prop, value);
        }
    }

    bool TryGetUVUnderCursor(out Vector2 uv)
    {
        uv = default;
        float radius = GetMapRadius();
        bool sphereModeActive = mapMaterial.GetFloat("_Sphere") > 0.5f;

        Vector2 screenPos = input.Map.Point.ReadValue<Vector2>();
        Ray sRay = cam.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));
        CollectTargetRenderers();

        bool hasHit = false;
        float closestDistance = float.PositiveInfinity;
        Vector2 bestUV = default;

        for (int i = 0; i < targetRenderers.Count; i++)
        {
            Renderer target = targetRenderers[i];
            if (!target) continue;

            if (!TryGetUVOnRenderer(target, sRay, sphereModeActive, radius, out Vector2 candidateUV, out float hitDistance))
            {
                continue;
            }

            if (hitDistance < closestDistance)
            {
                closestDistance = hitDistance;
                bestUV = candidateUV;
                hasHit = true;
            }
        }

        if (!hasHit) return false;
        uv = bestUV;
        return true;
    }

    void CollectTargetRenderers()
    {
        targetRenderers.Clear();
        if (rend) targetRenderers.Add(rend);

        if (additionalMapRenderers == null) return;
        for (int i = 0; i < additionalMapRenderers.Length; i++)
        {
            Renderer candidate = additionalMapRenderers[i];
            if (!candidate || candidate == rend) continue;
            if (!targetRenderers.Contains(candidate))
            {
                targetRenderers.Add(candidate);
            }
        }
    }

    bool TryGetUVOnRenderer(Renderer target, Ray screenRay, bool sphereModeActive, float radius, out Vector2 uv, out float distanceWs)
    {
        uv = default;
        distanceWs = float.PositiveInfinity;

        Transform tr = target.transform;
        Vector3 ro = tr.InverseTransformPoint(screenRay.origin);
        Vector3 rd = tr.InverseTransformDirection(screenRay.direction).normalized;

        Vector3 hitLocal;
        if (sphereModeActive)
        {
            if (!TryIntersectSphere(ro, rd, radius, out hitLocal))
            {
                return false;
            }

            float lon = Mathf.Atan2(hitLocal.z, hitLocal.x);
            float lat = Mathf.Asin(Mathf.Clamp(hitLocal.y / radius, -1f, 1f));

            float u = (lon / (2f * Mathf.PI)) + 0.5f;
            float v = (lat / Mathf.PI) + 0.5f;
            uv = new Vector2(u, v);
        }
        else
        {
            if (!TryIntersectPlanarMap(target, ro, rd, out Vector2 mapXY, out hitLocal))
            {
                return false;
            }

            if (!TryProjectUVFromAitoff(new Vector3(mapXY.x, mapXY.y, 0f), radius, out uv))
            {
                return false;
            }
        }

        Vector3 hitWs = tr.TransformPoint(hitLocal);
        distanceWs = Vector3.Dot(hitWs - screenRay.origin, screenRay.direction);
        return distanceWs > 0f;
    }

    static bool TryIntersectSphere(Vector3 ro, Vector3 rd, float radius, out Vector3 hitPoint)
    {
        hitPoint = default;
        float bTerm = Vector3.Dot(ro, rd);
        float cTerm = Vector3.Dot(ro, ro) - radius * radius;
        float discriminant = bTerm * bTerm - cTerm;
        if (discriminant < 0f) return false;

        float sqrtD = Mathf.Sqrt(discriminant);
        float hitDistance = -bTerm - sqrtD;
        if (hitDistance <= 0f) hitDistance = -bTerm + sqrtD;
        if (hitDistance <= 0f) return false;

        hitPoint = ro + rd * hitDistance;
        return true;
    }

    static bool TryIntersectPlanarMap(Renderer target, Vector3 ro, Vector3 rd, out Vector2 mapXY, out Vector3 hitPoint)
    {
        mapXY = default;
        hitPoint = default;

        MeshFilter meshFilter = target.GetComponent<MeshFilter>();
        Mesh mesh = meshFilter ? meshFilter.sharedMesh : null;
        if (!mesh) return false;

        int normalAxis = GetNormalAxis(mesh.bounds.extents);

        const float EPS = 1e-6f;
        float roN = GetAxis(ro, normalAxis);
        float rdN = GetAxis(rd, normalAxis);
        if (Mathf.Abs(rdN) < EPS) return false;

        float t = -roN / rdN;
        if (t <= 0f) return false;

        hitPoint = ro + rd * t;
        if (!PointInMeshBoundsOnPlane(mesh.bounds, hitPoint, normalAxis))
        {
            return false;
        }

        mapXY = GetMapXY(hitPoint, normalAxis);
        return true;
    }

    static int GetNormalAxis(Vector3 extents)
    {
        if (extents.x <= extents.y && extents.x <= extents.z) return 0;
        if (extents.y <= extents.x && extents.y <= extents.z) return 1;
        return 2;
    }

    static float GetAxis(Vector3 v, int axis)
    {
        if (axis == 0) return v.x;
        if (axis == 1) return v.y;
        return v.z;
    }

    static bool PointInMeshBoundsOnPlane(Bounds b, Vector3 p, int normalAxis)
    {
        const float eps = 1e-5f;
        if (normalAxis != 0 && (p.x < b.min.x - eps || p.x > b.max.x + eps)) return false;
        if (normalAxis != 1 && (p.y < b.min.y - eps || p.y > b.max.y + eps)) return false;
        if (normalAxis != 2 && (p.z < b.min.z - eps || p.z > b.max.z + eps)) return false;
        return true;
    }

    static Vector2 GetMapXY(Vector3 p, int normalAxis)
    {
        if (normalAxis == 2) return new Vector2(p.x, p.y); // XY plane
        if (normalAxis == 1) return new Vector2(p.x, p.z); // XZ plane
        return new Vector2(p.z, p.y);                       // YZ plane fallback
    }

    float GetMapRadius()
    {
        if (mapMaterial != null && mapMaterial.HasProperty("_Radius"))
        {
            return Mathf.Max(1e-6f, mapMaterial.GetFloat("_Radius"));
        }
        return 100f;
    }

    bool TryProjectUVFromAitoff(Vector3 p, float radius, out Vector2 uv)
    {
        uv = default;
        float morph = mapMaterial.GetFloat("_Morph"); // 0=equirectangular, 1=aitoff
        if (!TryInverseAitoffBlended(new Vector2(p.x, p.y), morph, radius, out float lat, out float lon))
        {
            return false;
        }

        float v = (lat / Mathf.PI) + 0.5f;
        if (v < 0f || v > 1f) return false;

        float u = (lon / (2f * Mathf.PI)) + 0.5f;
        uv = new Vector2(u, v);
        return true;
    }

    bool TryInverseAitoffBlended(Vector2 targetXY, float morph, float radius, out float latitude, out float longitude)
    {
        latitude = Mathf.Clamp(targetXY.y / radius, -Mathf.PI * 0.5f, Mathf.PI * 0.5f);
        longitude = Mathf.Clamp(targetXY.x / radius, -Mathf.PI, Mathf.PI);

        const float eps = 1e-4f;
        // Residual is in world units; scale tolerance with radius so convergence remains stable at large map scales.
        float tolerance = Mathf.Max(1e-4f, radius * 1e-6f);
        for (int i = 0; i < 12; i++)
        {
            Vector2 f = ProjectAitoffBlended(latitude, longitude, morph, radius) - targetXY;
            if (f.sqrMagnitude < tolerance * tolerance)
            {
                return true;
            }

            Vector2 fLatPlus = ProjectAitoffBlended(latitude + eps, longitude, morph, radius);
            Vector2 fLatMinus = ProjectAitoffBlended(latitude - eps, longitude, morph, radius);
            Vector2 fLonPlus = ProjectAitoffBlended(latitude, longitude + eps, morph, radius);
            Vector2 fLonMinus = ProjectAitoffBlended(latitude, longitude - eps, morph, radius);

            Vector2 dF_dLat = (fLatPlus - fLatMinus) * (0.5f / eps);
            Vector2 dF_dLon = (fLonPlus - fLonMinus) * (0.5f / eps);

            float det = dF_dLat.x * dF_dLon.y - dF_dLat.y * dF_dLon.x;
            if (Mathf.Abs(det) < 1e-6f)
            {
                break;
            }

            float invDet = 1f / det;
            float deltaLat = (-f.x * dF_dLon.y + f.y * dF_dLon.x) * invDet;
            float deltaLon = (-dF_dLat.x * f.y + dF_dLat.y * f.x) * invDet;

            latitude = Mathf.Clamp(latitude + deltaLat, -Mathf.PI * 0.5f, Mathf.PI * 0.5f);
            longitude = Mathf.Repeat(longitude + deltaLon + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
        }

        Vector2 finalError = ProjectAitoffBlended(latitude, longitude, morph, radius) - targetXY;
        return finalError.sqrMagnitude < tolerance * tolerance;
    }

    Vector2 ProjectAitoffBlended(float latitude, float longitude, float morph, float radius)
    {
        float cosPhi1 = 2f / Mathf.PI; // Winkel Tripel standard parallel
        Vector2 equirect = new Vector2(longitude * radius * cosPhi1, latitude * radius);
        Vector2 aitoff = ProjectAitoff(latitude, longitude, radius);
        return Vector2.Lerp(equirect, aitoff, Mathf.Clamp01(morph));
    }

    Vector2 ProjectAitoff(float latitude, float longitude, float radius)
    {
        float halfLon = 0.5f * longitude;
        float cosLat = Mathf.Cos(latitude);
        float sinLat = Mathf.Sin(latitude);
        float cosHalfLon = Mathf.Cos(halfLon);
        float sinHalfLon = Mathf.Sin(halfLon);
        float alpha = Mathf.Acos(Mathf.Clamp(cosLat * cosHalfLon, -1f, 1f));
        float sinAlpha = Mathf.Sin(alpha);
        float invSinc = Mathf.Abs(alpha) < 1e-6f ? 1f : (alpha / sinAlpha);

        float x = 2f * cosLat * sinHalfLon * invSinc * radius;
        float y = sinLat * invSinc * radius;
        return new Vector2(x, y);
    }

    int SampleProvinceId(float u, float v)
    {
        int x = Mathf.Clamp(Mathf.FloorToInt(u * texW), 0, texW - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(v * texH), 0, texH - 1);
        Color32 c = idPixels[y * texW + x];
        return c.r | (c.g << 8) | (c.b << 16);
    }
}

using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Renderer))]
public class ProvincePickerAitoff : MonoBehaviour
{
    [Header("Refs")]
    public Camera cam;                 // your WorldMapController camera
    public Material mapMaterial;       // same material you set _Morph/_UVOffset on
    public Texture2D provinceIdTex;    // readable, point, no mips

    [Header("Geometry")]
    public float radius = 100f;        // must match your controller/shader R

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
        if (mapMaterial)
        {
            mapMaterial.SetTexture("_ProvinceIDTex", provinceIdTex);
            // Initialize selected province ID to -1 (no selection)
            mapMaterial.SetInt(selectedIdProp, -1);
        }
    }

    void OnEnable() => input.Enable();
    void OnDisable() => input.Disable();

    void Update()
    {
        if (!cam || !mapMaterial) return;

        if (TryGetUVUnderCursor(out Vector2 uv))
        {
            // Apply the same sampling offset as your shader (horizontal repeat)
            Vector2 uvOffset = mapMaterial.GetVector("_UVOffset"); // focusLon/360
            float u = uv.x + uvOffset.x;
            float v = uv.y + uvOffset.y; // you keep Y = 0; still safe
            u = u - Mathf.Floor(u);      // repeat X
            v = Mathf.Clamp01(v);        // clamp Y

            int pid = SampleProvinceId(u, v);

            // Block ocean/background selection/hover
            if (blockOcean && pid == oceanId)
            {
                if (mapMaterial) mapMaterial.SetInt(hoverIdProp, -1);
                // Ignore clicks on ocean
                return;
            }

            if (highlightHovered)
            {
                mapMaterial.SetInt(hoverIdProp, pid);             // live hover id
                mapMaterial.SetColor(hoverColorProp, hoverColor);  // hover color
            }

            if (input.Map.LMB.WasPressedThisFrame())
            {
                mapMaterial.SetInt(selectedIdProp, pid);               // commit selection
                mapMaterial.SetColor(highlightColorProp, highlightColor);
                Debug.Log($"Clicked province ID = {pid}");
            }
        }
        else
        {
            // No hover
            if (mapMaterial) mapMaterial.SetInt(hoverIdProp, -1);
        }
    }

    bool TryGetUVUnderCursor(out Vector2 uv)
    {
        uv = default;

        // Ray in MAP LOCAL space (so math matches your object-space projection)
        Vector2 screenPos = input.Map.Point.ReadValue<Vector2>();
        Ray sRay = cam.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));
        Transform tr = rend.transform;
        Vector3 ro = tr.InverseTransformPoint(sRay.origin);
        Vector3 rd = tr.InverseTransformDirection(sRay.direction).normalized;

        // Intersect plane z=0
        const float EPS = 1e-6f;
        if (Mathf.Abs(rd.z) < EPS) return false;
        float t = -ro.z / rd.z;
        if (t <= 0f) return false;

        Vector3 p = ro + rd * t;

        return TryProjectUVFromAitoff(p, out uv);
    }

    bool TryProjectUVFromAitoff(Vector3 p, out Vector2 uv)
    {
        uv = default;
        float morph = mapMaterial.GetFloat("_Morph"); // 0=equirectangular, 1=aitoff
        if (!TryInverseAitoffBlended(new Vector2(p.x, p.y), morph, out float lat, out float lon))
        {
            return false;
        }

        float v = (lat / Mathf.PI) + 0.5f;
        if (v < 0f || v > 1f) return false;

        float u = (lon / (2f * Mathf.PI)) + 0.5f;
        uv = new Vector2(u, v);
        return true;
    }

    bool TryInverseAitoffBlended(Vector2 targetXY, float morph, out float latitude, out float longitude)
    {
        latitude = Mathf.Clamp(targetXY.y / radius, -Mathf.PI * 0.5f, Mathf.PI * 0.5f);
        longitude = Mathf.Clamp(targetXY.x / radius, -Mathf.PI, Mathf.PI);

        const float eps = 1e-4f;
        const float tolerance = 1e-4f;
        for (int i = 0; i < 8; i++)
        {
            Vector2 f = ProjectAitoffBlended(latitude, longitude, morph) - targetXY;
            if (f.sqrMagnitude < tolerance * tolerance)
            {
                return true;
            }

            Vector2 fLatPlus = ProjectAitoffBlended(latitude + eps, longitude, morph);
            Vector2 fLatMinus = ProjectAitoffBlended(latitude - eps, longitude, morph);
            Vector2 fLonPlus = ProjectAitoffBlended(latitude, longitude + eps, morph);
            Vector2 fLonMinus = ProjectAitoffBlended(latitude, longitude - eps, morph);

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

        Vector2 finalError = ProjectAitoffBlended(latitude, longitude, morph) - targetXY;
        return finalError.sqrMagnitude < tolerance * tolerance;
    }

    Vector2 ProjectAitoffBlended(float latitude, float longitude, float morph)
    {
        Vector2 equirect = new Vector2(longitude * radius, latitude * radius);
        Vector2 aitoff = ProjectAitoff(latitude, longitude);
        return Vector2.Lerp(equirect, aitoff, Mathf.Clamp01(morph));
    }

    Vector2 ProjectAitoff(float latitude, float longitude)
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

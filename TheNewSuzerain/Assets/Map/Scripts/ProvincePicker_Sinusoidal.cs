using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Renderer))]
public class ProvincePicker_Sinusoidal : MonoBehaviour
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

        // Convert plane position to lat (y) and lon (x), then to UV.
        // Equirectangular: x = lon * R, y = lat * R
        // Sinusoidal:      x = lon * R * cos(lat), y = lat * R
        float lat = p.y / radius;
        float v = (lat / Mathf.PI) + 0.5f;
        if (v < 0f || v > 1f) return false;

        float morph = mapMaterial.GetFloat("_Morph"); // 0=equirectangular, 1=sinusoidal
        float widthFactor = Mathf.Lerp(1f, Mathf.Cos(lat), morph);
        if (widthFactor < 1e-6f)
        {
            if (Mathf.Abs(p.x) > 1e-4f) return false;
            widthFactor = 1f;
        }

        float lon = p.x / (radius * widthFactor);
        float u = (lon / (2f * Mathf.PI)) + 0.5f;
        if (u < 0f || u > 1f) return false;

        uv = new Vector2(u, v);
        return true;
    }

    int SampleProvinceId(float u, float v)
    {
        int x = Mathf.Clamp(Mathf.FloorToInt(u * texW), 0, texW - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(v * texH), 0, texH - 1);
        Color32 c = idPixels[y * texW + x];
        return c.r | (c.g << 8) | (c.b << 16);
    }
}

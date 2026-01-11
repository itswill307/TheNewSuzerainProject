using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Renderer))]
public class ProvincePicker : MonoBehaviour
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

        // Ray in MAP LOCAL space (so math matches your object-space morph)
        Vector2 screenPos = input.Map.Point.ReadValue<Vector2>();
        Ray sRay = cam.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));
        Transform tr = rend.transform;
        Vector3 ro = tr.InverseTransformPoint(sRay.origin);
        Vector3 rd = tr.InverseTransformDirection(sRay.direction).normalized;

        float morph = mapMaterial.GetFloat("_Morph");   // you set this each frame
        float rEff  = Mathf.Lerp(radius * 3f, radius, morph); // same as controller
        // Sphere center like your controller (tangent at z=0 when morphed)
        Vector3 C = new Vector3(0f, 0f, rEff);

        // --- Initial guess: plane when flatter, sphere when rounder ---
        Vector2 uv0 = default;
        bool gotInit = false;

        if (morph < 0.5f)
        {
            // Plane z=0
            const float EPS = 1e-6f;
            if (Mathf.Abs(rd.z) >= EPS)
            {
                float t = -ro.z / rd.z;
                if (t > 0f)
                {
                    Vector3 p = ro + rd * t;
                    uv0 = new Vector2(
                        p.x / (2f * Mathf.PI * radius) + 0.5f,
                        p.y / (Mathf.PI * radius) + 0.5f
                    );
                    gotInit = true;
                }
            }
        }
        if (!gotInit)
        {
            // Sphere: ||ro + t rd - C|| = rEff
            Vector3 o = ro - C;
            float A = Vector3.Dot(rd, rd);
            float B = 2f * Vector3.Dot(o, rd);
            float D = B * B - 4f * A * (Vector3.Dot(o, o) - rEff * rEff);
            if (D < 0f) return false;
            float t0 = (-B - Mathf.Sqrt(D)) / (2f * A);
            float t1 = (-B + Mathf.Sqrt(D)) / (2f * A);
            float tHit = (t0 > 0f) ? t0 : (t1 > 0f ? t1 : -1f);
            if (tHit <= 0f) return false;

            Vector3 p = ro + rd * tHit;
            Vector3 q = p - C; // on sphere
            float lat = Mathf.Asin(Mathf.Clamp(q.y / rEff, -1f, 1f));
            // Inverse of x=cos(lat)*sin(lon), z=-cos(lat)*cos(lon)
            float lon = Mathf.Atan2(q.x, -q.z);

            uv0 = new Vector2(lon / (2f * Mathf.PI) + 0.5f, lat / Mathf.PI + 0.5f);
            gotInit = true;
        }

        // Ensure we have a valid seed
        if (!gotInit) return false;

        // Clamp/wrap the seed
        uv0 = new Vector2(uv0.x - Mathf.Floor(uv0.x), Mathf.Clamp01(uv0.y));

        // --- Refinement: full 3×3 Newton solve for (t,u,v) ---
        {
            float u = uv0.x;
            float v = uv0.y;
            // initialize t by projecting current surface point onto ray
            Vector3 S0 = PosFromUV(uv0, morph, rEff, C);
            float t = Vector3.Dot(S0 - ro, rd);

            for (int i = 0; i < 8; i++)
            {
                Vector2 uvCur = new Vector2(u, v);
                Vector3 S = PosFromUV(uvCur, morph, rEff, C);
                Vector3 Su, Sv;
                TangentsFromUV(uvCur, morph, rEff, out Su, out Sv);

                Vector3 F = ro + rd * t - S; // want F=0
                if (F.sqrMagnitude < 1e-8f) break;

                // Build 3x3 system: [rd, -Su, -Sv] * [dt, du, dv] = -F
                Vector3 c0 = rd;
                Vector3 c1 = -Su;
                Vector3 c2 = -Sv;

                if (!Solve3x3(c0, c1, c2, -F, out Vector3 d)) break;

                t += d.x;
                u += d.y;
                v = Mathf.Clamp01(v + d.z);

                // wrap u softly to keep continuity
                if (u < 0f) u -= Mathf.Floor(u);
                else if (u > 1f) u -= Mathf.Floor(u);
            }

            uv = new Vector2(u - Mathf.Floor(u), v);
        }
        return true;
    }

    // Solve A * x = b where A has columns a,b,c (3x3)
    bool Solve3x3(Vector3 a, Vector3 b, Vector3 c, Vector3 rhs, out Vector3 x)
    {
        // Row-major matrix
        float m00 = a.x, m01 = b.x, m02 = c.x;
        float m10 = a.y, m11 = b.y, m12 = c.y;
        float m20 = a.z, m21 = b.z, m22 = c.z;

        float det = m00*(m11*m22 - m12*m21) - m01*(m10*m22 - m12*m20) + m02*(m10*m21 - m11*m20);
        if (Mathf.Abs(det) < 1e-10f) { x = default; return false; }
        float inv = 1f / det;

        // Inverse via adjugate
        float i00 =  (m11*m22 - m12*m21) * inv;
        float i01 = -(m01*m22 - m02*m21) * inv;
        float i02 =  (m01*m12 - m02*m11) * inv;
        float i10 = -(m10*m22 - m12*m20) * inv;
        float i11 =  (m00*m22 - m02*m20) * inv;
        float i12 = -(m00*m12 - m02*m10) * inv;
        float i20 =  (m10*m21 - m11*m20) * inv;
        float i21 = -(m00*m21 - m01*m20) * inv;
        float i22 =  (m00*m11 - m01*m10) * inv;

        x = new Vector3(
            i00*rhs.x + i01*rhs.y + i02*rhs.z,
            i10*rhs.x + i11*rhs.y + i12*rhs.z,
            i20*rhs.x + i21*rhs.y + i22*rhs.z
        );
        return true;
    }

    Vector3 PosFromUV(Vector2 uv, float morph, float rEff, Vector3 C)
    {
        float u = uv.x, v = uv.y;
        // Plane
        Vector3 Pp = new Vector3(
            (u - 0.5f) * (2f * Mathf.PI * radius),
            (v - 0.5f) * (Mathf.PI * radius),
            0f
        );
        // Sphere (your orientation)
        float lon = (u - 0.5f) * (2f * Mathf.PI);
        float lat = (v - 0.5f) * (Mathf.PI);
        Vector3 Ps = C + rEff * new Vector3(
            Mathf.Cos(lat) * Mathf.Sin(lon),
            Mathf.Sin(lat),
            -Mathf.Cos(lat) * Mathf.Cos(lon)
        );
        return Vector3.Lerp(Pp, Ps, morph);
    }

    void TangentsFromUV(Vector2 uv, float morph, float rEff, out Vector3 Su, out Vector3 Sv)
    {
        float u = uv.x, v = uv.y;
        // Plane tangents
        Vector3 Pu = new Vector3(2f * Mathf.PI * radius, 0f, 0f);
        Vector3 Pv = new Vector3(0f, Mathf.PI * radius, 0f);

        // Sphere tangents
        float lon = (u - 0.5f) * (2f * Mathf.PI);
        float lat = (v - 0.5f) * (Mathf.PI);
        float dLon = 2f * Mathf.PI;
        float dLat = Mathf.PI;

        Vector3 Su_sphere = rEff * new Vector3(
            Mathf.Cos(lat) * Mathf.Cos(lon) * dLon,
            0f,
            Mathf.Cos(lat) * Mathf.Sin(lon) * dLon
        );

        Vector3 Sv_sphere = rEff * new Vector3(
            -Mathf.Sin(lat) * Mathf.Sin(lon) * dLat,
            Mathf.Cos(lat) * dLat,
            Mathf.Sin(lat) * Mathf.Cos(lon) * dLat
        );

        Su = Vector3.Lerp(Pu, Su_sphere, morph);
        Sv = Vector3.Lerp(Pv, Sv_sphere, morph);
    }

    int SampleProvinceId(float u, float v)
    {
        int x = Mathf.Clamp(Mathf.FloorToInt(u * texW), 0, texW - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(v * texH), 0, texH - 1);
        Color32 c = idPixels[y * texW + x];
        return c.r | (c.g << 8) | (c.b << 16);
    }
}

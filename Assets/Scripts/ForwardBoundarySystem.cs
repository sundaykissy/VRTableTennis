using UnityEngine;

public enum BoundaryVisualizationMode
{
    EdgeStripOnly,
    WallOnly,
    Both
}

public class ForwardBoundarySystem : MonoBehaviour
{
    [Header("Debug / Testing (disable before data collection)")]
    public bool forceActiveForTesting = false;

    [Header("Scene References — Assign All in Inspector")]
    [SerializeField] private Collider tableCollider;
    [SerializeField] private Renderer tableSurfaceRenderer;
    [SerializeField] private Transform playerSideReference;
    [SerializeField] private Transform enemySideReference;
    [SerializeField] private Transform tableRoot;

    [Header("General")]
    public bool enableBoundary = true;
    public BoundaryVisualizationMode visualizationMode = BoundaryVisualizationMode.EdgeStripOnly;
    public float boundaryDistanceFromTable = 0.08f;
    public float transitionSpeed = 6f;

    [Header("Detection Thresholds")]
    public float warningDistance = 0.15f;
    public float violationDistance = 0.10f;

    [Header("Colors")]
    public Color safeColor = new Color(0.10f, 0.90f, 0.20f, 1f);
    public Color warningColor = new Color(1.00f, 0.65f, 0.00f, 1f);
    public Color violationColor = new Color(1.00f, 0.00f, 0.00f, 1f);

    [Header("Pulse")]
    public float pulseIntensity = 0.25f;
    public float pulseDuration = 0.45f;

    [Header("Edge Strip")]
    public float edgeStripSafeOpacity = 0.08f;
    public float edgeStripWarningOpacity = 0.30f;
    public float edgeStripViolationOpacity = 0.70f;

    [Header("Wall Plane")]
    public float boundaryWidth = 1.8f;
    public float boundaryHeight = 2.2f;
    public float safeOpacity = 0.07f;
    public float warningOpacity = 0.30f;
    public float violationOpacity = 0.55f;

    [Header("Tabletop Tint")]
    public bool enableTableTint = true;
    [Range(0f, 1f)] public float tableTintStrength = 0.45f;

    private GameObject _wallGO;
    private MeshRenderer _wallRenderer;
    private Material _wallMat;

    private GameObject _stripGO;
    private MeshRenderer _stripRenderer;
    private Material _stripMat;

    private Material[] _tableMats;
    private Color[] _tableOriginalColors;
    private int[] _tableColorProps;
    private bool _tableReady;

    private Vector3 _boundaryWorldCenter;
    private Vector3 _boundaryNormal;

    private float _smoothT = 0f;
    private float _pulseTimer = 0f;
    private bool _wasBeyond = false;
    private bool _initialized = false;
    private bool _wasActive = false;
    private string _lastState = "";
    private bool _wasViolating = false;

    public static event System.Action<float> OnBoundaryViolation;

    private bool ShowWall =>
        visualizationMode == BoundaryVisualizationMode.WallOnly ||
        visualizationMode == BoundaryVisualizationMode.Both;

    private bool ShowEdgeStrip =>
        visualizationMode == BoundaryVisualizationMode.EdgeStripOnly ||
        visualizationMode == BoundaryVisualizationMode.Both;

    private bool BoundaryActive =>
        enableBoundary &&
        (forceActiveForTesting ||
         (ExperimentLogger.SessionActive &&
          (ExperimentLogger.GroupNumber == 2 || ExperimentLogger.GroupNumber == 3)));

    void Start()
    {
        _initialized = Initialize();
    }

    void OnDestroy()
    {
        RestoreTableColor();

        if (_wallMat != null) Destroy(_wallMat);
        if (_wallGO != null) Destroy(_wallGO);
        if (_stripMat != null) Destroy(_stripMat);
        if (_stripGO != null) Destroy(_stripGO);
    }

    bool Initialize()
    {
        bool ok = true;

        if (tableCollider == null)
        {
            Debug.LogError("[Boundary] tableCollider is not assigned!");
            ok = false;
        }

        if (playerSideReference == null)
        {
            Debug.LogError("[Boundary] playerSideReference is not assigned!");
            ok = false;
        }

        if (enemySideReference == null)
        {
            Debug.LogError("[Boundary] enemySideReference is not assigned!");
            ok = false;
        }

        if (!ok)
        {
            enabled = false;
            return false;
        }

        SetupTableMaterials();
        BuildWall();
        BuildEdgeStrip();

        SyncStripToTable();

        Debug.Log($"[Boundary] Initialized. boundaryCenter={_boundaryWorldCenter}");

        return true;
    }

    void SetupTableMaterials()
    {
        _tableReady = false;

        if (tableSurfaceRenderer == null)
        {
            Debug.LogWarning("[Boundary] tableSurfaceRenderer is not assigned — tabletop tint disabled.");
            return;
        }

        Material[] mats = tableSurfaceRenderer.materials;
        _tableMats = mats;
        _tableOriginalColors = new Color[mats.Length];
        _tableColorProps = new int[mats.Length];

        int readyCount = 0;

        for (int i = 0; i < mats.Length; i++)
        {
            Material m = mats[i];
            if (m == null) continue;

            if (m.HasProperty("_BaseColor"))
                _tableColorProps[i] = Shader.PropertyToID("_BaseColor");
            else
                _tableColorProps[i] = Shader.PropertyToID("_Color");

            _tableOriginalColors[i] = m.GetColor(_tableColorProps[i]);
            readyCount++;
        }

        _tableReady = readyCount > 0;
    }

    void BuildWall()
    {
        _wallGO = new GameObject("ForwardBoundaryPlane");

        MeshFilter wf = _wallGO.AddComponent<MeshFilter>();
        wf.mesh = BuildWallQuad(boundaryWidth, boundaryHeight);

        _wallRenderer = _wallGO.AddComponent<MeshRenderer>();
        _wallRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _wallRenderer.receiveShadows = false;
        _wallRenderer.allowOcclusionWhenDynamic = false;

        _wallMat = CreateUnlitMaterial();
        _wallRenderer.material = _wallMat;

        Color c = safeColor;
        c.a = safeOpacity;
        _wallMat.color = c;

        _wallGO.SetActive(false);
    }

    void BuildEdgeStrip()
    {
        _stripGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _stripGO.name = "ForwardBoundaryStrip";

        Collider c = _stripGO.GetComponent<Collider>();
        if (c != null) Destroy(c);

        _stripRenderer = _stripGO.GetComponent<MeshRenderer>();
        _stripRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _stripRenderer.receiveShadows = false;
        _stripRenderer.allowOcclusionWhenDynamic = false;

        _stripMat = CreateUnlitMaterial();
        _stripRenderer.material = _stripMat;

        Color sc = safeColor;
        sc.a = edgeStripSafeOpacity;
        _stripMat.color = sc;

        _stripGO.SetActive(false);
    }

    void LateUpdate()
    {
        if (!_initialized) return;
        SyncStripToTable();
    }

    Vector3 GetTableForward()
    {
        if (playerSideReference != null && enemySideReference != null)
        {
            Vector3 v = enemySideReference.position - playerSideReference.position;
            v.y = 0f;

            if (v.sqrMagnitude > 1e-6f)
                return v.normalized;
        }

        if (tableRoot != null)
        {
            Vector3 f = tableRoot.forward;
            f.y = 0f;

            if (f.sqrMagnitude > 1e-6f)
                return f.normalized;
        }

        return Vector3.forward;
    }

    public void RefreshFromTable()
    {
        SyncStripToTable();

        Debug.Log($"[BOUNDARY-RECENTER] refreshed after table recenter " +
                  $"tablePos={(tableRoot != null ? tableRoot.position : Vector3.zero)} " +
                  $"boundaryPos={_boundaryWorldCenter}");
    }

    void SyncStripToTable()
    {
        if (tableCollider == null) return;

        Bounds b = tableCollider.bounds;
        Vector3 fwd = GetTableForward();

        Vector3 up = tableRoot != null ? tableRoot.up : Vector3.up;
        up = up.normalized;

        Vector3 right = Vector3.Cross(up, fwd).normalized;

        float halfFwd = Mathf.Abs(b.extents.x * fwd.x)
                      + Mathf.Abs(b.extents.y * fwd.y)
                      + Mathf.Abs(b.extents.z * fwd.z);

        float halfW = Mathf.Abs(b.extents.x * right.x)
                    + Mathf.Abs(b.extents.y * right.y)
                    + Mathf.Abs(b.extents.z * right.z);

        Vector3 nearEdge = b.center - fwd * halfFwd;

        _boundaryWorldCenter = nearEdge - fwd * boundaryDistanceFromTable;
        _boundaryNormal = fwd;

        if (_stripGO != null)
        {
            _stripGO.transform.position = new Vector3(
                nearEdge.x,
                b.max.y + 0.025f,
                nearEdge.z
            );

            _stripGO.transform.localScale = new Vector3(halfW * 2f, 0.05f, 1f);
            _stripGO.transform.rotation = Quaternion.LookRotation(fwd, up);
        }

        if (_wallGO != null)
        {
            Vector3 wallPos = _boundaryWorldCenter;
            wallPos.y = boundaryHeight * 0.5f;

            _wallGO.transform.position = wallPos;
            _wallGO.transform.localScale = Vector3.one;
            _wallGO.transform.rotation = Quaternion.LookRotation(fwd, up);
        }
    }

    static Mesh BuildWallQuad(float width, float height)
    {
        Mesh mesh = new Mesh { name = "BoundaryWall" };

        float hw = width * 0.5f;
        float hh = height * 0.5f;

        mesh.vertices = new Vector3[]
        {
            new Vector3(-hw, -hh, 0f),
            new Vector3( hw, -hh, 0f),
            new Vector3(-hw,  hh, 0f),
            new Vector3( hw,  hh, 0f)
        };

        mesh.uv = new Vector2[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f)
        };

        mesh.triangles = new int[]
        {
            0, 2, 1,
            1, 2, 3,
            0, 1, 2,
            1, 3, 2
        };

        mesh.RecalculateNormals();
        return mesh;
    }

    static Material CreateUnlitMaterial()
    {
        Shader s = Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Transparent")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Standard");

        Material mat = new Material(s);
        mat.renderQueue = 3000;
        return mat;
    }

    void ApplyTableTint(float violationT)
    {
        if (!_tableReady || _tableMats == null) return;

        for (int i = 0; i < _tableMats.Length; i++)
        {
            if (_tableMats[i] == null) continue;

            Color target = Color.Lerp(
                _tableOriginalColors[i],
                violationColor,
                violationT * tableTintStrength
            );

            _tableMats[i].SetColor(_tableColorProps[i], target);
        }
    }

    void RestoreTableColor()
    {
        if (!_tableReady || _tableMats == null) return;

        for (int i = 0; i < _tableMats.Length; i++)
        {
            if (_tableMats[i] == null) continue;
            _tableMats[i].SetColor(_tableColorProps[i], _tableOriginalColors[i]);
        }
    }

    void Update()
    {
        if (!_initialized) return;

        if (!BoundaryActive)
        {
            if (_wasActive)
            {
                _smoothT = Mathf.Lerp(_smoothT, 0f, Time.deltaTime * transitionSpeed);

                if (ShowWall) ApplyWallVisuals(_smoothT);
                if (ShowEdgeStrip) ApplyStripVisuals(_smoothT);

                float recoveryViolationT = Mathf.Clamp01((_smoothT - 0.5f) * 2f);
                if (enableTableTint) ApplyTableTint(recoveryViolationT);

                if (_smoothT < 0.005f)
                {
                    if (ShowWall && _wallGO != null) _wallGO.SetActive(false);
                    if (ShowEdgeStrip && _stripGO != null) _stripGO.SetActive(false);

                    RestoreTableColor();

                    _wasActive = false;
                    _lastState = "";
                }
            }

            return;
        }

        if (!_wasActive)
        {
            if (ShowWall && _wallGO != null) _wallGO.SetActive(true);
            if (ShowEdgeStrip && _stripGO != null) _stripGO.SetActive(true);

            _wasActive = true;
        }

        Camera hmd = Camera.main;
        if (hmd == null) return;

        float penetration = Vector3.Dot(
            hmd.transform.position - _boundaryWorldCenter,
            _boundaryNormal
        );

        float totalRange = Mathf.Max(0.01f, warningDistance + violationDistance);
        float rawT = Mathf.Clamp01((penetration + warningDistance) / totalRange);

        _smoothT = Mathf.Lerp(_smoothT, rawT, Time.deltaTime * transitionSpeed);

        bool nowBeyond = penetration >= 0f;

        if (nowBeyond && !_wasBeyond)
            _pulseTimer = pulseDuration;

        _wasBeyond = nowBeyond;

        if (_pulseTimer > 0f)
            _pulseTimer -= Time.deltaTime;

        string state = _smoothT < 0.05f ? "SAFE"
                     : _smoothT < 0.5f ? "APPROACHING"
                     : "VIOLATION";

        if (state != _lastState)
        {
            Debug.Log($"[Boundary] {state} smoothT={_smoothT:F3} " +
                      $"penetration={penetration:F3} " +
                      $"hmdPos={hmd.transform.position} " +
                      $"boundaryCenter={_boundaryWorldCenter}");

            _lastState = state;
        }

        if (ShowWall) ApplyWallVisuals(_smoothT);
        if (ShowEdgeStrip) ApplyStripVisuals(_smoothT);

        if (enableTableTint)
        {
            float violationT = Mathf.Clamp01((_smoothT - 0.5f) * 2f);
            ApplyTableTint(violationT);
        }

        float hapticIntensity = Mathf.Clamp01((_smoothT - 0.5f) * 2f);

        if (hapticIntensity > 0f)
            OnBoundaryViolation?.Invoke(hapticIntensity);

        bool nowViolating = hapticIntensity > 0f;

        if (nowViolating && !_wasViolating)
        {
            ExperimentLogger.Instance?.LogBoundaryViolation(true, hapticIntensity);
        }
        else if (!nowViolating && _wasViolating)
        {
            ExperimentLogger.Instance?.LogBoundaryViolation(false, 0f);
        }

        _wasViolating = nowViolating;
    }

    void ApplyWallVisuals(float t)
    {
        if (_wallMat == null) return;

        Color c;
        float a;

        if (t <= 0.5f)
        {
            float t2 = t * 2f;
            c = Color.Lerp(safeColor, warningColor, t2);
            a = Mathf.Lerp(safeOpacity, warningOpacity, t2);
        }
        else
        {
            float t2 = (t - 0.5f) * 2f;
            c = Color.Lerp(warningColor, violationColor, t2);
            a = Mathf.Lerp(warningOpacity, violationOpacity, t2);
        }

        float pf = _pulseTimer > 0f ? _pulseTimer / pulseDuration : 0f;
        c.a = Mathf.Clamp01(a + pf * pulseIntensity);

        _wallMat.color = c;
    }

    void ApplyStripVisuals(float t)
    {
        if (_stripMat == null) return;

        Color c;
        float a;

        if (t <= 0.5f)
        {
            float t2 = t * 2f;
            c = Color.Lerp(safeColor, warningColor, t2);
            a = Mathf.Lerp(edgeStripSafeOpacity, edgeStripWarningOpacity, t2);
        }
        else
        {
            float t2 = (t - 0.5f) * 2f;
            c = Color.Lerp(warningColor, violationColor, t2);
            a = Mathf.Lerp(edgeStripWarningOpacity, edgeStripViolationOpacity, t2);
        }

        float pf = _pulseTimer > 0f ? _pulseTimer / pulseDuration : 0f;
        c.a = Mathf.Clamp01(a + pf * pulseIntensity);

        _stripMat.color = c;
    }
}

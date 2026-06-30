// BallTrajectoryPredictor.cs
// Predictive ball trajectory — VR depth-perception aid for serve-training study.
//
// Shows a short (≤ 0.5 s) predicted arc ONLY while the ball travels toward the
// player and the session is active.  Groups 2 and 3 only.
//
// Research context:
//   Compensates for VR stereo depth-perception limitations that make it hard to
//   judge incoming ball distance and timing before interception.
//   NOT a coaching system, NOT an aiming assistant, NOT a target predictor.
//
// Performance:
//   Single LineRenderer.  Pre-allocated Vector3[N_POINTS] scratch buffer.
//   Zero per-frame heap allocation.  positionCount never changes at runtime.
//   Quest 3 friendly.
//
// DO NOT modify ball physics, gameplay.cs, ballbounce.cs, or
// ExperimentLogger.cs from this script.

using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class BallTrajectoryPredictor : MonoBehaviour
{
    public static BallTrajectoryPredictor Instance { get; private set; }

    // ── Simulation constants ───────────────────────────────────────────────────
    private const int   N_POINTS    = 20;       // arc segments — no runtime alloc
    private const float SIM_DT      = 0.025f;   // step size: 20 × 0.025 = 0.5 s ahead
    private const float MIN_SPEED   = 0.6f;     // m/s — below this, hide the arc
    private const float MIN_Z_VEL   = 0.10f;    // minimum Z component toward player
    private const float GRAVITY     = -9.81f;
    private const float LIN_DAMPING = 0.02f;    // matches gameplay.cs linearDamping

    // ── Visual constants ───────────────────────────────────────────────────────
    // Soft cyan-white, tapers from START_WIDTH down to END_WIDTH and fades out.
    private const float START_WIDTH = 0.007f;
    private const float END_WIDTH   = 0.001f;
    private static readonly Color ARC_COLOR = new Color(0.75f, 0.95f, 1.00f);  // RGB only — alpha via gradient

    // ── Runtime references ─────────────────────────────────────────────────────
    private LineRenderer _lr;
    private Vector3[]    _pts = new Vector3[N_POINTS];   // pre-allocated, reused each frame

    private Rigidbody _ballRb;
    private float     _tableTopY  = 0.76f;
    private float     _playerDirZ = 1f;    // sign of "toward player" along world Z

    private bool _conditionOn = false;
    private bool _sceneReady  = false;

    // ─────────────────────────────────────────────────────────────────────────
    // Auto-instantiation — fires before any scene Awake
    // ─────────────────────────────────────────────────────────────────────────
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void AutoCreate()
    {
        var go = new GameObject("[BallTrajectoryPredictor]");
        go.AddComponent<BallTrajectoryPredictor>();
        DontDestroyOnLoad(go);
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        BuildLineRenderer();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void Start()
    {
        if (IsGameScene(SceneManager.GetActiveScene().name))
            StartCoroutine(CacheNextFrame());
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Match VisualFeedbackManager's group convention:
    ///   1 = Standard VR  → OFF
    ///   2 = Visual       → ON
    ///   3 = Multimodal   → ON
    /// </summary>
    public void SetCondition(int group)
    {
        _conditionOn = group >= 2;
        if (!_conditionOn) Hide();
        Debug.Log($"[BallTrajectoryPredictor] SetCondition group={group} → on={_conditionOn}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scene lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _sceneReady = false;
        _ballRb     = null;
        Hide();

        if (IsGameScene(scene.name))
            StartCoroutine(CacheNextFrame());
    }

    IEnumerator CacheNextFrame()
    {
        yield return null;   // wait one frame — scene objects fully initialised
        CacheSceneRefs();
    }

    void CacheSceneRefs()
    {
        _tableTopY  = 0.76f;
        _playerDirZ = 1f;

        var ps = GameObject.Find("playerside");
        var es = GameObject.Find("enemyside");

        if (ps != null)
        {
            var col = ps.GetComponent<Collider>();
            if (col != null) _tableTopY = col.bounds.max.y;
        }

        if (ps != null && es != null)
        {
            float dir = Mathf.Sign(ps.transform.position.z - es.transform.position.z);
            _playerDirZ = (dir == 0f) ? 1f : dir;
        }

        // Ball Rigidbody via gameplay's public ball reference.
        var gp = FindObjectOfType<gameplay>();
        if (gp != null && gp.ball != null)
            _ballRb = gp.ball.GetComponent<Rigidbody>();

        _sceneReady = (_ballRb != null);

        Debug.Log($"[BallTrajectoryPredictor] CacheSceneRefs — tableTopY={_tableTopY:F3} " +
                  $"playerDirZ={_playerDirZ:F0} " +
                  $"ballRb={(_sceneReady ? "OK" : "NOT FOUND")}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-frame update
    // ─────────────────────────────────────────────────────────────────────────

    void LateUpdate()
    {
        // ── Gate checks ───────────────────────────────────────────────────────
        if (!_conditionOn || !_sceneReady || _lr == null) { Hide(); return; }
        if (!ExperimentLogger.SessionActive)               { Hide(); return; }
        if (_ballRb == null || _ballRb.isKinematic)        { Hide(); return; }

        Vector3 vel   = _ballRb.linearVelocity;
        float   speed = vel.magnitude;

        // Show only while ball travels toward the player above the speed threshold.
        bool movingTowardPlayer = vel.z * _playerDirZ > MIN_Z_VEL;
        if (!movingTowardPlayer || speed < MIN_SPEED) { Hide(); return; }

        // ── Simulate and display ──────────────────────────────────────────────
        Simulate(_ballRb.position, vel);
        _lr.enabled = true;
        _lr.SetPositions(_pts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Physics simulation
    // ─────────────────────────────────────────────────────────────────────────
    // Gravity + linear air damping only — spec says ignore spin initially.
    // Stops extending the arc when the ball would reach the table surface or
    // reverse its Z direction; pads remaining points at the stop position so
    // positionCount never needs to change (avoids any per-frame GPU resize).

    void Simulate(Vector3 pos, Vector3 vel)
    {
        for (int i = 0; i < N_POINTS; i++)
        {
            _pts[i] = pos;

            // Integrate one step.
            vel.y += GRAVITY * SIM_DT;
            vel   *= (1f - LIN_DAMPING * SIM_DT);
            pos   += vel * SIM_DT;

            // Terminate early: table surface hit or Z-direction reversal.
            bool hitTable  = pos.y <= _tableTopY;
            bool reversedZ = vel.z * _playerDirZ < 0f;

            if (hitTable || reversedZ)
            {
                // Pad all remaining slots with the last valid position.
                // positionCount stays N_POINTS — no GPU buffer resize.
                for (int j = i + 1; j < N_POINTS; j++)
                    _pts[j] = _pts[i];
                return;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LineRenderer construction
    // ─────────────────────────────────────────────────────────────────────────

    void BuildLineRenderer()
    {
        _lr = gameObject.AddComponent<LineRenderer>();
        _lr.positionCount        = N_POINTS;    // fixed forever — never changed at runtime
        _lr.useWorldSpace        = true;
        _lr.shadowCastingMode    = UnityEngine.Rendering.ShadowCastingMode.Off;
        _lr.receiveShadows       = false;
        _lr.generateLightingData = false;       // saves per-vertex normal/tangent work

        // Width: tapers from START_WIDTH at the ball end to END_WIDTH at the far end.
        _lr.widthMultiplier = START_WIDTH;
        _lr.widthCurve      = new AnimationCurve(
            new Keyframe(0f, 1f),
            new Keyframe(1f, END_WIDTH / START_WIDTH));

        // Colour gradient: uniform hue, fades from ARC_COLOR.a to fully transparent.
        var grad = new Gradient();
        grad.SetKeys(
            new GradientColorKey[]
            {
                new GradientColorKey(ARC_COLOR, 0f),
                new GradientColorKey(ARC_COLOR, 1f),
            },
            new GradientAlphaKey[]
            {
                new GradientAlphaKey(0.32f, 0f),
                new GradientAlphaKey(0.00f, 1f),
            });
        _lr.colorGradient = grad;

        // Unlit alpha-blended material — same fallback chain as VisualFeedbackManager.
        Shader sh = Shader.Find("Sprites/Default")
                 ?? Shader.Find("Unlit/Transparent")
                 ?? Shader.Find("Unlit/Color");
        _lr.material = new Material(sh) { renderQueue = 3000 };

        // Initialise positions so the buffer is never uninitialized.
        for (int i = 0; i < N_POINTS; i++) _pts[i] = Vector3.zero;
        _lr.SetPositions(_pts);

        _lr.enabled = false;   // hidden until the first valid prediction
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    void Hide()
    {
        if (_lr != null && _lr.enabled) _lr.enabled = false;
    }

    static bool IsGameScene(string name)
        => name.Contains("game") || name.Contains("room");
}

// HitVisualizer.cs
// Per-hit debug visualizer for VR serve training.
// Renders contact normal, ball trajectories, paddle surface velocity, face normal,
// and overlap-recovery events as world-space LineRenderers visible in the HMD.
//
// USAGE
//   Attach to any persistent GameObject in the scene (e.g. the table or a
//   dedicated DebugVisualizer empty).  No further setup required.
//   Toggle runtime visibility with showVisuals in the Inspector.
//
// COLOUR KEY
//   Yellow  — contact normal (outward from paddle face)
//   Magenta — paddle face normal (pFwd)
//   Cyan    — incoming ball velocity (scaled)
//   Green   — outgoing ball velocity (scaled)
//   White   — effective paddle surface velocity at contact point (scaled)
//   Orange  — reposition vector (contactPt → repoBallPos)
//   Red     — contact point cross marker
//   Amber   — overlap recovery entry (ComputePenetration path)

using System.Collections.Generic;
using UnityEngine;

public class HitVisualizer : MonoBehaviour
{
    public static HitVisualizer Instance { get; private set; }

    [Header("Visibility")]
    [Tooltip("Master on/off toggle — can be changed at runtime.")]
    public bool showVisuals = true;

    [Header("Timing")]
    [Tooltip("Seconds each element remains visible.")]
    public float lingerSeconds = 2.5f;

    [Header("Scale")]
    [Tooltip("Length of the contact normal ray (metres).")]
    public float normalLength  = 0.14f;
    [Tooltip("Metres of ray per m/s of velocity.")]
    public float velocityScale = 0.035f;
    [Tooltip("Half-length of the contact-point cross marker (metres).")]
    public float markerHalf    = 0.012f;
    [Tooltip("LineRenderer tube diameter (metres).")]
    public float lineWidth     = 0.003f;

    // ── Colours ────────────────────────────────────────────────────────────
    static readonly Color ColNormal  = Color.yellow;
    static readonly Color ColFace    = new Color(1f, 0f, 1f);         // magenta
    static readonly Color ColBallIn  = Color.cyan;
    static readonly Color ColBallOut = Color.green;
    static readonly Color ColPaddle  = Color.white;
    static readonly Color ColRepo    = new Color(1f, 0.45f, 0f);      // orange
    static readonly Color ColContact = Color.red;
    static readonly Color ColOverlap = new Color(1f, 0.6f, 0.1f);    // amber

    // Plain struct — avoids value-tuple dependency (C# 7 / .NET Standard 2.0+).
    struct VizEntry
    {
        public GameObject go;
        public float      expiry;
    }

    readonly List<VizEntry> _active = new List<VizEntry>(64);
    Material _mat;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        _mat           = new Material(Shader.Find("Sprites/Default"));
        _mat.hideFlags = HideFlags.HideAndDontSave;
    }

    void Update()
    {
        float now = Time.time;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (now >= _active[i].expiry)
            {
                if (_active[i].go) Destroy(_active[i].go);
                _active.RemoveAt(i);
            }
        }
    }

    void OnDestroy()
    {
        for (int i = 0; i < _active.Count; i++)
            if (_active[i].go) Destroy(_active[i].go);
        _active.Clear();
        if (_mat) Destroy(_mat);
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>Called from gameplay.ApplyPaddleHit after ball repositioning.</summary>
    public void RecordHit(
        Vector3 contactPt,
        Vector3 contactNormal,
        Vector3 ballVelIn,
        Vector3 ballVelOut,
        Vector3 effectiveVel,
        Vector3 paddleFaceNormal,
        Vector3 repoBallPos,
        string  source)
    {
        if (!showVisuals) return;
        float expiry = Time.time + lingerSeconds;

        // Contact point — red cross
        DrawLine(contactPt - Vector3.right   * markerHalf, contactPt + Vector3.right   * markerHalf, ColContact, expiry);
        DrawLine(contactPt - Vector3.up      * markerHalf, contactPt + Vector3.up      * markerHalf, ColContact, expiry);
        DrawLine(contactPt - Vector3.forward * markerHalf, contactPt + Vector3.forward * markerHalf, ColContact, expiry);

        // Contact normal — yellow
        DrawRay(contactPt, contactNormal    * normalLength,             ColNormal,  expiry);
        // Paddle face normal — magenta (shorter)
        DrawRay(contactPt, paddleFaceNormal * (normalLength * 0.65f),  ColFace,    expiry);
        // Ball incoming — cyan
        DrawRay(contactPt, ballVelIn        * velocityScale,            ColBallIn,  expiry);
        // Ball outgoing — green
        DrawRay(contactPt, ballVelOut       * velocityScale,            ColBallOut, expiry);
        // Effective surface vel — white
        DrawRay(contactPt, effectiveVel     * velocityScale,            ColPaddle,  expiry);
        // Reposition offset — orange
        DrawLine(contactPt, repoBallPos,                                ColRepo,    expiry);
    }

    /// <summary>Called from gameplay.cs ComputePenetration path at overlap detection.</summary>
    public void RecordOverlapEntry(
        Vector3 ballPosAtDetect,
        Vector3 sepDir,
        float   sepDist)
    {
        if (!showVisuals) return;
        float expiry = Time.time + lingerSeconds;

        DrawRay(ballPosAtDetect, sepDir * (sepDist + 0.06f), ColOverlap, expiry);
        DrawLine(ballPosAtDetect - Vector3.right * markerHalf,
                 ballPosAtDetect + Vector3.right * markerHalf, ColOverlap, expiry);
        DrawLine(ballPosAtDetect - Vector3.up    * markerHalf,
                 ballPosAtDetect + Vector3.up    * markerHalf, ColOverlap, expiry);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    void DrawRay(Vector3 origin, Vector3 dir, Color col, float expiry)
        => DrawLine(origin, origin + dir, col, expiry);

    void DrawLine(Vector3 a, Vector3 b, Color col, float expiry)
    {
        var go = new GameObject("HitViz") { hideFlags = HideFlags.HideInHierarchy };
        var lr = go.AddComponent<LineRenderer>();
        lr.material          = _mat;
        lr.startColor        = col;
        lr.endColor          = col;
        lr.startWidth        = lineWidth;
        lr.endWidth          = lineWidth;
        lr.positionCount     = 2;
        lr.useWorldSpace     = true;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        lr.SetPosition(0, a);
        lr.SetPosition(1, b);
        _active.Add(new VizEntry { go = go, expiry = expiry });
    }
}

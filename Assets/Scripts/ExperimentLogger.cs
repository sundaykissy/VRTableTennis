using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Singleton CSV logger for the VR table tennis SERVE TRAINING study.
/// Auto-instantiates at runtime — no scene attachment required.
/// Writes one file per session to Application.persistentDataPath/ExperimentLogs/
///
/// STUDY GROUPS:
///   1 = Standard VR   2 = Visual-only   3 = Multimodal (visual + haptics)
///
/// CSV COLUMNS (30):
///   Timestamp, ElapsedSec, ParticipantID, Group, GroupName, Session,
///   ServeID, TrialID, EventType,
///   BallSpeedMs, SpinRadS, SpinGenerated,
///   SuccessfulServe, TargetZoneID, TargetHit, LandingSide,
///   PaddleAngleErrorDeg, PaddleTiltDeg, MinPaddleTableDistM,
///   PaddleTableContact, PaddleTablePenetrationM,
///   BoundaryViolation, BoundaryIntensity,
///   VisualGuidesEnabled, SpinTrailEnabled, DepthGridEnabled,
///   WoojerEnabled, ControllerHapticEnabled, HapticEventType,
///   Notes
///
/// EVENT TYPES:
///   SessionStart       — once at session open; Notes contains [FEATURE-DIAG]
///   ServeStart         — at trigger release (toss)
///   PlayerHit          — paddle strikes ball
///   BallLanding        — ball contacts table (PlayerSide/OpponentSide), floor, or net
///                        OpponentSide rows include SuccessfulServe/TargetHit/TargetZoneID
///   PaddleTableContact — paddle crosses below table surface
///   BoundaryViolation  — HMD enters (BoundaryViolation=1) or exits (0) violation zone
///   HapticEvent        — Woojer or controller haptic fired (Group 3 only)
///   TrialEnd           — serve attempt complete (zone result or ball lost)
///   SessionEnd         — on app close / scene unload
///
/// DESIGN NOTES:
///   TrialID = ServeID (same counter; both columns present for explicit join in R/Python).
///   Feedback exposure flags (VisualGuidesEnabled, WoojerEnabled, etc.) are derived
///   from GroupNumber inside WriteEvent — no calling script needs to pass them.
///   BoundaryViolation fires on the ENTERING and EXITING edge only, not every frame.
///   HapticEvent rows are Group 3 only (WoojerEnabled / ControllerHapticEnabled = 1).
/// </summary>
public class ExperimentLogger : MonoBehaviour
{
    // ── Singleton ─────────────────────────────────────────────────────────────
    public static ExperimentLogger Instance { get; private set; }

    // ── Session metadata (set before StartSession) ────────────────────────────
    public static int  ParticipantID = 0;
    public static int  GroupNumber   = 1;   // 1=Standard  2=Visual  3=Multimodal
    public static int  SessionNumber = 1;
    public static bool SessionActive = false;

    // ── Serve zone result event ────────────────────────────────────────────────
    // Fires once per serve after zone classification.
    // Consumed by WoojerHapticManager (Group 3 only). Safe to leave unsubscribed.
    public static event System.Action<bool> OnServeZoneResult;

    // ── Internal ──────────────────────────────────────────────────────────────
    private StreamWriter _writer;
    private float        _sessionStart;
    private int          _serveId      = 0;
    private string       _logPath;

    // Per-trial state
    private float  _trialStartTime  = 0f;
    private bool   _trialInProgress = false;

    // Staged zone result: written by ServeZoneClassifier (via LogServeZoneResult)
    // inside the OnTableBounce event, then consumed by the subsequent LogBallLanding call.
    private bool   _stagedZoneSuccess = false;
    private string _stagedZoneID      = "";
    private bool   _hasStaged         = false;

    // ── Auto-instantiation ────────────────────────────────────────────────────
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void AutoCreate()
    {
        var go = new GameObject("[ExperimentLogger]");
        go.AddComponent<ExperimentLogger>();
        DontDestroyOnLoad(go);
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance      = this;
        SessionActive = false;
        _writer       = null;

#if UNITY_EDITOR
        StartSession(participantID: 0, group: 1, session: 0);
        Debug.Log("[ExperimentLogger] EDITOR: session auto-started (ID=0, Group=1).");
#endif
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Call when researcher presses Start on the login screen.</summary>
    public void StartSession(int participantID, int group, int session)
    {
        ParticipantID    = participantID;
        GroupNumber      = group;
        SessionNumber    = session;
        SessionActive    = true;
        _serveId         = 0;
        _sessionStart    = Time.time;
        _trialInProgress = false;
        _hasStaged       = false;

        string dir = Path.Combine(Application.persistentDataPath, "ExperimentLogs");
        Directory.CreateDirectory(dir);

        _logPath = Path.Combine(dir,
            $"P{participantID:D3}_{GroupTag(group)}_S{session}.csv");

        bool isNewFile = !File.Exists(_logPath);
        _writer = new StreamWriter(_logPath, append: true);

        if (isNewFile)
        {
            _writer.WriteLine(
                "Timestamp,ElapsedSec,ParticipantID,Group,GroupName,Session," +
                "ServeID,TrialID,EventType," +
                "BallSpeedMs,SpinRadS,SpinGenerated," +
                "SuccessfulServe,TargetZoneID,TargetHit,LandingSide," +
                "PaddleAngleErrorDeg,PaddleTiltDeg,MinPaddleTableDistM," +
                "PaddleTableContact,PaddleTablePenetrationM," +
                "BoundaryViolation,BoundaryIntensity," +
                "VisualGuidesEnabled,SpinTrailEnabled,DepthGridEnabled," +
                "WoojerEnabled,ControllerHapticEnabled,HapticEventType," +
                "Notes");
        }
        _writer.Flush();

        Debug.Log($"[ExperimentLogger] Session started → {_logPath}");

        bool visual  = group >= 2;
        bool haptics = group == 3;
        string diag  =
            $"[FEATURE-DIAG] group={group}" +
            $" visual={(visual  ? "ON" : "OFF")}" +
            $" spinTrail={(visual  ? "ON" : "OFF")}" +
            $" depthGrid={(visual  ? "ON" : "OFF")}" +
            $" woojer={(haptics ? "ON" : "OFF")}" +
            $" controllerHaptic={(haptics ? "ON" : "OFF")}";
        Debug.Log(diag);

        WriteEvent("SessionStart", notes: diag.Replace(",", ";"));
    }

    /// <summary>
    /// Ball tossed — marks the start of a new serve attempt.
    /// Call at trigger release; increments ServeID.
    /// </summary>
    public void LogServeStart()
    {
        _serveId++;
        _trialStartTime  = Time.time;
        _trialInProgress = true;
        _hasStaged       = false;

        WriteEvent("ServeStart", notes: $"serve={_serveId}");
    }

    /// <summary>
    /// Player paddle made contact with the ball.
    /// spinRadS     = tangential brush velocity (m/s) — paddle technique quality.
    /// spinGenerated = resulting ball angular speed (rad/s) after this hit.
    /// </summary>
    public void LogPlayerHit(float ballSpeedMs, float spinRadS, float spinGenerated,
                             float paddleAngleErrorDeg, float paddleTiltDeg,
                             float minPaddleTableDistM)
    {
        WriteEvent("PlayerHit",
            ballSpeedMs:         ballSpeedMs,
            spinRadS:            spinRadS,
            spinGenerated:       spinGenerated,
            paddleAngleErrorDeg: paddleAngleErrorDeg,
            paddleTiltDeg:       paddleTiltDeg,
            minPaddleTableDistM: minPaddleTableDistM,
            notes:
                $"spd={ballSpeedMs:F2} spinBrush={spinRadS:F1} spinOut={spinGenerated:F1} " +
                $"angleErr={paddleAngleErrorDeg:F1} tilt={paddleTiltDeg:F1} " +
                $"minDist={minPaddleTableDistM:F3}");
    }

    /// <summary>
    /// Ball made contact with the table (player or opponent side), floor, or net.
    /// Must be called AFTER LogServeZoneResult (which stages the zone outcome) for
    /// OpponentSide landings so that SuccessfulServe/TargetHit are populated.
    /// </summary>
    public void LogBallLanding(string landingSide, float ballSpeedMs, float spinRadS)
    {
        int    success = _hasStaged && _stagedZoneSuccess ? 1 : 0;
        string zoneID  = _hasStaged ? _stagedZoneID : "";
        int    hit     = success;
        _hasStaged = false;

        WriteEvent("BallLanding",
            ballSpeedMs:     ballSpeedMs,
            spinRadS:        spinRadS,
            successfulServe: success,
            targetZoneID:    zoneID,
            targetHit:       hit,
            landingSide:     landingSide,
            notes:
                $"side={landingSide} spd={ballSpeedMs:F2} spin={spinRadS:F1} " +
                $"success={success} zone={(zoneID.Length > 0 ? zoneID : "-")}");
    }

    /// <summary>
    /// Called by ServeZoneClassifier BEFORE LogBallLanding to stage the zone outcome.
    /// Also fires OnServeZoneResult for downstream haptic consumers (WoojerHapticManager).
    /// Does NOT write a CSV row — the outcome is embedded in the BallLanding row.
    /// </summary>
    public void LogServeZoneResult(bool success, string targetZoneID = "unassigned")
    {
        _stagedZoneSuccess = success;
        _stagedZoneID      = targetZoneID;
        _hasStaged         = true;

        OnServeZoneResult?.Invoke(success);
    }

    /// <summary>Paddle physically crossed below the table surface.</summary>
    public void LogPaddleTableContact(float penetrationM)
    {
        WriteEvent("PaddleTableContact",
            paddleTableContact: 1,
            paddleTablePeneM:   penetrationM,
            notes: $"depth={penetrationM:F4}m");
    }

    /// <summary>
    /// HMD boundary violation state changed.
    /// entering=true  → player just crossed into violation (BoundaryViolation=1).
    /// entering=false → player just left violation zone (BoundaryViolation=0).
    /// Only fires on the edge — not every frame.
    /// </summary>
    public void LogBoundaryViolation(bool entering, float intensity)
    {
        WriteEvent("BoundaryViolation",
            boundaryViolation: entering ? 1 : 0,
            boundaryIntensity: intensity,
            notes: $"state={(entering ? "enter" : "exit")} intensity={intensity:F2}");
    }

    /// <summary>
    /// A haptic device fired (Group 3 only).
    /// hapticEventType examples: "Woojer_PaddleImpact", "Controller_PaddleHit",
    ///   "Woojer_ServeFail", "Woojer_PaddleTable", "Controller_PaddleTable"
    /// </summary>
    public void LogHapticEvent(string hapticEventType)
    {
        WriteEvent("HapticEvent",
            hapticEventType: hapticEventType,
            notes: hapticEventType);
    }

    /// <summary>
    /// Marks the end of a trial (zone result or ball lost).
    /// Guards against double-fire — safe to call from multiple paths.
    /// </summary>
    public void LogTrialEnd()
    {
        if (!_trialInProgress) return;
        _trialInProgress = false;

        float duration = Time.time - _trialStartTime;
        WriteEvent("TrialEnd",
            notes: $"serve={_serveId} duration={duration:F2}s");
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    static string GroupTag(int g) =>
        g == 1 ? "Standard" : g == 2 ? "Visual" : "Multimodal";

    void WriteEvent(string eventType,
                    float  ballSpeedMs         = 0f,
                    float  spinRadS            = 0f,
                    float  spinGenerated       = 0f,
                    int    successfulServe     = 0,
                    string targetZoneID        = "",
                    int    targetHit           = 0,
                    string landingSide         = "",
                    float  paddleAngleErrorDeg = 0f,
                    float  paddleTiltDeg       = 0f,
                    float  minPaddleTableDistM = 0f,
                    int    paddleTableContact  = 0,
                    float  paddleTablePeneM    = 0f,
                    int    boundaryViolation   = 0,
                    float  boundaryIntensity   = 0f,
                    string hapticEventType     = "",
                    string notes               = "")
    {
        if (_writer == null || !SessionActive) return;

        float elapsed = Time.time - _sessionStart;
        int   g       = GroupNumber;
        bool  visual  = g >= 2;
        bool  haptics = g == 3;

        // Feedback exposure flags — derived from group; no caller needs to pass them.
        int visualGuides     = visual  ? 1 : 0;
        int spinTrail        = visual  ? 1 : 0;
        int depthGrid        = visual  ? 1 : 0;
        int woojer           = haptics ? 1 : 0;
        int ctrlHaptic       = haptics ? 1 : 0;

        string line = string.Join(",", new string[]
        {
            DateTime.Now.ToString("HH:mm:ss.fff"),
            elapsed.ToString("F3"),
            ParticipantID.ToString(),
            g.ToString(),
            GroupTag(g),
            SessionNumber.ToString(),
            _serveId.ToString(),
            _serveId.ToString(),           // TrialID = ServeID
            eventType,
            ballSpeedMs.ToString("F3"),
            spinRadS.ToString("F1"),
            spinGenerated.ToString("F1"),
            successfulServe.ToString(),
            targetZoneID.Replace(",", ";"),
            targetHit.ToString(),
            landingSide,
            paddleAngleErrorDeg.ToString("F1"),
            paddleTiltDeg.ToString("F1"),
            minPaddleTableDistM.ToString("F3"),
            paddleTableContact.ToString(),
            paddleTablePeneM.ToString("F4"),
            boundaryViolation.ToString(),
            boundaryIntensity.ToString("F2"),
            visualGuides.ToString(),
            spinTrail.ToString(),
            depthGrid.ToString(),
            woojer.ToString(),
            ctrlHaptic.ToString(),
            hapticEventType,
            notes.Replace(",", ";")
        });

        _writer.WriteLine(line);
        _writer.Flush();
    }

    void OnDestroy()
    {
        if (_writer != null)
        {
            if (_trialInProgress) LogTrialEnd();
            WriteEvent("SessionEnd");
            _writer.Close();
            _writer = null;
        }
    }
}

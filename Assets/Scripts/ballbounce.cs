using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ballbounce : MonoBehaviour
{
    // ── Inspector ────────────────────────────────────────────────────────────
    // enemyPaddle is kept as an Inspector slot so the existing scene reference
    // compiles cleanly; it is immediately deactivated in gameplay.Start() and
    // never used during training mode.
    public GameObject enemyPaddle;

    // ── Statics ──────────────────────────────────────────────────────────────
    public static int  followPlayer;
    public static bool enemyFlag;
    public static bool resetFlag;

    // ── Table contact event ───────────────────────────────────────────────────
    // Fires on every validated table contact (playerside or enemyside).
    // Args: collider name, ball world position at contact.
    // Not currently subscribed by WoojerHapticManager — reserved for future use.
    public static event System.Action<string, Vector3> OnTableBounce;

    // ── Game state ───────────────────────────────────────────────────────────
    // Training mode uses only two states:
    //   playerStart  — ball in hand, waiting to be served
    //   playerPaddle — ball in play (bouncing freely)
    // All scoring / rally states have been removed.
    public enum gameState
    {
        playerStart,
        playerPaddle
    }
    public static gameState state;

    // ── Physics constants ────────────────────────────────────────────────────
    //
    // Unity physics (via PhysicsMaterial on ball + table) now handles the
    // primary Y-velocity reflection.  These constants control only the spin
    // contribution applied in OnCollisionEnter.
    //
    // TABLE_FRICTION raised 0.25→0.30 (calibration pass): stronger spin-to-velocity
    // kick at table contact makes topspin/backspin visibly distinguishable.
    private const float TABLE_FRICTION = 0.30f;   // spin-to-horizontal transfer
    // SPIN_DECAY raised 0.58→0.72 (calibration pass): spin now survives 72% through
    // each bounce (was 58%), so backspin persists long enough to produce a visible
    // check after the first table contact (was nearly gone by the second bounce).
    private const float SPIN_DECAY     = 0.72f;   // fraction of spin retained per bounce
    private const float BALL_RADIUS    = 0.02f;   // real ping-pong ball radius (m)
    private const float MAGNUS_COEFF   = 5.5e-6f; // Magnus / topspin lift coefficient

    // ── Net geometry ─────────────────────────────────────────────────────────
    private const float NET_HEIGHT    = 0.1525f;
    private const float NET_THICKNESS = 0.025f;

    // ── Private ──────────────────────────────────────────────────────────────
    private Rigidbody _ballRb;
    private string    _curCol;
    private float     _lastBounceTime  = -10f;

    // Velocity-sign bounce detection (FixedUpdate)
    private float _prevVelY     = 0f;
    private float _lastBounceY  = -999f; // world Y at last detected bounce

    // ── Audio ─────────────────────────────────────────────────────────────────
    private AudioSource _audio;
    private AudioClip   _tableBounceClip;
    private AudioClip   _floorBounceClip;
    private AudioClip   _netHitClip;

    // ── Singleton ────────────────────────────────────────────────────────────
    private static ballbounce _instance;
    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("[ballbounce] Duplicate — destroying: " + gameObject.name);
            Destroy(gameObject);
            return;
        }
        _instance = this;

        _tableBounceClip = CreatePingClip(820f, 0.055f);
        _floorBounceClip = CreatePingClip(320f, 0.120f);
        _netHitClip      = CreatePingClip(220f, 0.120f);

        _audio = GetComponent<AudioSource>();
        if (_audio == null) _audio = gameObject.AddComponent<AudioSource>();
        // Fully 2D — VR 3D rolloff makes table-distance sounds inaudible.
        // spatialBlend=0 means volume is identical wherever the player looks.
        _audio.spatialBlend = 0f;
        _audio.rolloffMode  = AudioRolloffMode.Linear;
        _audio.minDistance  = 1f;
        _audio.maxDistance  = 500f;
        _audio.playOnAwake  = false;
        _audio.mute         = false;
        _audio.volume       = 1f;
    }

    private bool _sessionWasActive = false;

    // ── Start ────────────────────────────────────────────────────────────────
    void Start()
    {
        state        = gameState.playerStart;
        followPlayer = 0;
        enemyFlag    = false;
        resetFlag    = false;

        _ballRb = GetComponent<Rigidbody>();
        if (_ballRb != null)
        {
            _ballRb.useGravity = false;
            // Only write velocity while non-kinematic — guard against scene-saved kinematic state.
            if (!_ballRb.isKinematic)
            {
                _ballRb.linearVelocity  = Vector3.zero;
                _ballRb.angularVelocity = Vector3.zero;
            }
        }

        // ── Fix trigger sphere radius ─────────────────────────────────────────
        // The scene has two SphereColliders on the ball:
        //   solid   radius=0.5  (physics bounce — Unity handles this)
        //   trigger radius=1.5  (state machine events — us)
        //
        // The trigger was 3× the solid radius, so OnTriggerEnter was firing
        // when the ball was still 3× ball-width ABOVE the surface — causing the
        // manual bounce code to run at the wrong time.
        //
        // We shrink the trigger to just 2% larger than the solid sphere so both
        // fire simultaneously, and the event is usable for state/audio only.
        //
        SphereCollider solidSphere   = null;
        SphereCollider triggerSphere = null;
        foreach (var sc in GetComponents<SphereCollider>())
        {
            if (sc.isTrigger) triggerSphere = sc;
            else              solidSphere   = sc;
        }
        if (solidSphere != null && triggerSphere == null)
        {
            // No trigger sphere exists in the scene — add one at runtime.
            // OnTriggerEnter (state machine, table sounds, net detection) relies
            // on this collider; without it, all those callbacks are silent.
            triggerSphere           = gameObject.AddComponent<SphereCollider>();
            triggerSphere.isTrigger = true;
            triggerSphere.radius    = solidSphere.radius * 1.02f;
            Debug.Log($"[ballbounce] AUTO-ADDED trigger sphere radius={triggerSphere.radius:F3} " +
                      $"(solid={solidSphere.radius:F3}) — add a trigger SphereCollider " +
                      $"in the Inspector to suppress this message.");
        }
        else if (solidSphere != null && triggerSphere != null)
        {
            triggerSphere.radius = solidSphere.radius * 1.02f;
            Debug.Log($"[ballbounce] Trigger sphere fixed: {triggerSphere.radius:F3} " +
                      $"(solid={solidSphere.radius:F3})");
        }

        // ── Apply physics material to ball ────────────────────────────────────
        // Bounciness 0.88 matches real TT ball (COR ~0.88).
        // Maximum combine: the higher value across both surfaces wins, so the
        // ball's material dominates regardless of what the table has.
        if (solidSphere != null)
        {
            var ballMat = new PhysicsMaterial("Ball");
            ballMat.bounciness      = 0.88f;
            ballMat.dynamicFriction = 0.08f;
            ballMat.staticFriction  = 0.08f;
            ballMat.bounceCombine   = PhysicsMaterialCombine.Minimum;
            ballMat.frictionCombine = PhysicsMaterialCombine.Minimum;
            solidSphere.material    = ballMat;
        }

        // ── Apply physics material to table and floor ─────────────────────────
        // Table: real TT table COR ≈ 0.76.  With Maximum combine the ball's
        // 0.88 already wins, but having an explicit material prevents Unity's
        // default material (bounciness=0) from being used as a tiebreaker.
        ApplyEnvironmentMaterials();

        ParkBallUnderground();
        BuildNetTrigger();
    }

    // Assign physics materials to table and floor so Unity's solver uses
    // the correct COR without any manual velocity manipulation.
    void ApplyEnvironmentMaterials()
    {
        // Table surface: medium bounciness — ball's 0.88 wins via Maximum combine
        var tableMat = new PhysicsMaterial("Table");
        tableMat.bounciness      = 0.76f;
        tableMat.dynamicFriction = 0.22f;
        tableMat.staticFriction  = 0.22f;
        tableMat.bounceCombine   = PhysicsMaterialCombine.Minimum;
        tableMat.frictionCombine = PhysicsMaterialCombine.Minimum;

        // Floor: less bouncy than table (hardwood/carpet = lower COR)
        var floorMat = new PhysicsMaterial("Floor");
        floorMat.bounciness      = 0.55f;
        floorMat.dynamicFriction = 0.40f;
        floorMat.staticFriction  = 0.40f;
        floorMat.bounceCombine   = PhysicsMaterialCombine.Minimum;
        floorMat.frictionCombine = PhysicsMaterialCombine.Minimum;

        foreach (string surfaceName in new[] { "playerside", "enemyside" })
        {
            GameObject go = GameObject.Find(surfaceName);
            if (go == null) continue;
            foreach (var col in go.GetComponents<Collider>())
                if (!col.isTrigger) col.material = tableMat;
        }
        {
            GameObject go = GameObject.Find("floor");
            if (go != null)
                foreach (var col in go.GetComponents<Collider>())
                    if (!col.isTrigger) col.material = floorMat;
        }

        Debug.Log("[ballbounce] Physics materials applied to table and floor.");
    }

    // ── Ball park/show ────────────────────────────────────────────────────────
    //
    // Called ONLY from explicit serve-reset paths:
    //   • gameplay.cs trigger-press serve-reset (!inPlayerStart && triggerHeld)
    //   • Session start (first activation)
    //   • DoAIServe (teleport to AI paddle position)
    //
    // NOT called on scoring — scoring uses pointJustScored flag instead so
    // the ball keeps bouncing naturally after a point.
    //
    void ParkBallUnderground()
    {
        Debug.Log($"[ballbounce] ParkBallUnderground called — state={state}");
        if (_ballRb != null)
        {
            if (!_ballRb.isKinematic)
            {
                _ballRb.linearVelocity  = Vector3.zero;
                _ballRb.angularVelocity = Vector3.zero;
            }
            _ballRb.isKinematic = true;
            _ballRb.useGravity  = false;
        }
        transform.position = new Vector3(0f, -100f, 0f);
    }

    // ── Net trigger ───────────────────────────────────────────────────────────
    void BuildNetTrigger()
    {
        GameObject ps = GameObject.Find("playerside");
        GameObject es = GameObject.Find("enemyside");
        if (ps == null || es == null)
        {
            Debug.LogWarning("[ballbounce] Table sides not found — net skipped.");
            return;
        }

        Collider psCol  = ps.GetComponent<Collider>();
        float tableTopY = psCol != null ? psCol.bounds.max.y
                        : ps.transform.position.y + ps.transform.localScale.y * 0.5f;
        float netZ      = (ps.transform.position.z + es.transform.position.z) * 0.5f;
        float netX      = (ps.transform.position.x + es.transform.position.x) * 0.5f;
        float tblWidth  = psCol != null ? psCol.bounds.size.x : ps.transform.localScale.x;

        GameObject netGO = new GameObject("Net");
        netGO.transform.position = new Vector3(netX, tableTopY + NET_HEIGHT * 0.5f, netZ);
        BoxCollider nb = netGO.AddComponent<BoxCollider>();
        nb.isTrigger   = true;
        nb.size        = new Vector3(tblWidth + 0.4f, NET_HEIGHT, NET_THICKNESS);

        Debug.Log($"[ballbounce] Net z={netZ:F2} tableTop={tableTopY:F2}");
    }

    // ── Procedural audio ──────────────────────────────────────────────────────
    static AudioClip CreatePingClip(float frequency, float duration)
    {
        const int sampleRate = 44100;
        int   samples = Mathf.RoundToInt(sampleRate * duration);
        float[] data  = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t   = (float)i / sampleRate;
            float env = Mathf.Exp(-t * 38f);
            data[i]   = env * Mathf.Sin(2f * Mathf.PI * frequency * t);
        }
        var clip = AudioClip.Create("Ping_" + (int)frequency, samples, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ── FixedUpdate: Magnus lift + velocity-sign bounce sound detection ───────
    void FixedUpdate()
    {
        if (_ballRb == null) return;
        if (_ballRb.isKinematic)
        {
            _prevVelY = 0f;
            return;
        }

        float velY = _ballRb.linearVelocity.y;

        // ── Velocity-sign bounce detection ────────────────────────────────────
        // When Y-velocity flips from negative (falling) to positive (rising),
        // the ball just bounced off a surface.  This fires regardless of
        // collider names, trigger setup, or collision detection mode.
        if (_prevVelY < -0.8f && velY > 0.1f)
        {
            // Bounce detected by velocity sign only for diagnostics/state support.
            // Do NOT play audio here. Physical surface sounds are handled only by
            // OnCollisionEnter so one bounce cannot produce duplicate sounds.
            _lastBounceY = _ballRb.position.y;
        }

        _prevVelY = velY;

        // ── Magnus lift ───────────────────────────────────────────────────────
        if (!ExperimentLogger.SessionActive) return;
        _ballRb.AddForce(
            MAGNUS_COEFF * Vector3.Cross(_ballRb.angularVelocity, _ballRb.linearVelocity),
            ForceMode.Force);
    }

    // ── OnCollisionEnter: primary audio path for all surface contacts ────────
    //
    // This fires on the ball's SOLID-sphere collider making contact.
    // It is more reliable than OnTriggerEnter for sound because it doesn't
    // depend on the trigger sphere size or overlap timing.
    //
    // AUDIO ONLY — velocity is never modified here.  The PlaySound() cooldown
    // (_lastBounceTime, 0.04 s) prevents double-play when both this callback
    // and OnTriggerEnter fire in the same step.
    private int _collisionLogCount = 0;
    void OnCollisionEnter(Collision col)
    {
        if (_audio == null) return;
        string name = col.gameObject.name.ToLowerInvariant();
        // Paddle handled separately by gameplay.cs — don't duplicate.
        if (name.Contains("paddle") || name.Contains("hand")) return;

        float spd = col.relativeVelocity.magnitude;

        // Diagnostic: log the first 10 collision contacts.
        if (_collisionLogCount < 10)
        {
            Debug.Log($"[ballbounce] OnCollisionEnter #{_collisionLogCount}: " +
                      $"collider='{col.gameObject.name}' spd={spd:F2}");
            _collisionLogCount++;
        }

        if (spd < 0.15f) return;   // ignore true micro-contacts only

        bool isTable = name.Contains("side") || name.Contains("table");
        bool isFloor = name.Contains("floor") || name.Contains("ground")
                    || name.Contains("terrain");
        // Any unrecognised non-prop surface (wall, etc.) — treat as floor sound
        // rather than staying silent.  Walls/chair-legs tend to be very low-speed
        // contacts and are already filtered by the 0.15 m/s threshold above.
        bool isUnknown = !isTable && !isFloor
                      && !name.Contains("wall") && !name.Contains("chair")
                      && !name.Contains("ceiling") && !name.Contains("net");

        if      (isTable)   PlaySound(_tableBounceClip, spd, "TABLE_col");
        else if (isFloor)   PlaySound(_floorBounceClip, spd, "FLOOR_col");
        else if (isUnknown) PlaySound(_tableBounceClip, spd * 0.5f, "UNKNOWN_col");
    }

    private int _debugLog = 0;

    // ── State machine (Update) ────────────────────────────────────────────────
    void Update()
    {
        _debugLog++;

        if (!ExperimentLogger.SessionActive)
        {
            _sessionWasActive = false;
            return;
        }

        if (!_sessionWasActive)
        {
            _sessionWasActive = true;
            if (!gameplay.testModeActive) ParkBallUnderground();
            Debug.Log("[ballbounce] Session active — waiting for trigger.");
        }

        switch (state)
        {
            case gameState.playerStart:
                enemyFlag = false;
                resetFlag = true;
                // serveReady is set false at toss and true after 0.4 s by gameplay.cs.
                // This prevents a stale playerPaddleCollision flag (from the previous
                // rally) or a phantom hit from toss arm-motion from triggering the
                // playerStart → playerPaddle transition before the ball is in play.
                if (gameplay.playerPaddleCollision == 1)
                {
                    gameplay.playerPaddleCollision = 0;
                    if (gameplay.serveReady)
                    {
                        state = gameState.playerPaddle;
                        Debug.Log("[ballbounce] Serve hit → ball in play.");
                    }
                    else
                    {
                        Debug.Log("[ballbounce] playerPaddleCollision ignored — serveReady=false (debounce).");
                    }
                }
                break;

            case gameState.playerPaddle:
                enemyFlag = false;
                resetFlag = false;
                // Clear stale paddle collision flags; no scoring logic.
                if (gameplay.playerPaddleCollision == 1) gameplay.playerPaddleCollision = 0;
                if (gameplay.enemyPaddleCollision  == 1) gameplay.enemyPaddleCollision  = 0;
                break;
        }
    }

    // ── OnTriggerEnter — fired by the TRIGGER SphereCollider ─────────────────
    //
    // *** DO NOT MODIFY linearVelocity HERE ***
    //
    // The trigger sphere overlaps table/floor solid colliders.
    // Unity's physics engine handles the actual bounce through PhysicsMaterial.
    // Modifying velocity here would fight the physics solver (double-bounce).
    // Training mode: this callback handles AUDIO + LOGGING only.
    // No scoring, no state transitions based on surface contacts.
    //
    // Count triggers seen — used to emit a one-time diagnostic log so the user
    // can verify the exact collider names without flooding the log.
    private int _triggerLogCount = 0;

    void OnTriggerEnter(Collider col)
    {
        // Ball is kinematic while held in the player's hand — ignore all geometry
        // overlaps during that phase.  Without this guard the playerside collider
        // (which the hand sits inside at table height) fires spurious table-contact
        // sounds and logger events on every frame.
        if (_ballRb != null && _ballRb.isKinematic) return;

        _curCol = col.gameObject.name;

        // Diagnostic: log the first 10 unique collider names so the developer
        // can verify that "playerside" / "enemyside" / "floor" match the scene.
        if (_triggerLogCount < 10)
        {
            Debug.Log($"[ballbounce] OnTriggerEnter #{_triggerLogCount}: " +
                      $"collider='{_curCol}' layer={col.gameObject.layer} " +
                      $"isTrigger={col.isTrigger}");
            _triggerLogCount++;
        }

        // Audio plays regardless of session state — sounds should never be
        // silenced by experiment state (they are part of the core game feel).
        // State-machine transitions below ARE gated on SessionActive.

        // ── Table contacts: audio + spin transfer + logging ──────────────────
        // Name check is case-insensitive so scene objects named "PlayerSide" or
        // "Enemyside" etc. still fire correctly.  If your objects have different
        // names you will see them in the diagnostic log above.
        string nameLower = _curCol.ToLowerInvariant();
        bool isTable = nameLower == "playerside" || nameLower == "enemyside"
                    || nameLower.Contains("playerside") || nameLower.Contains("enemyside");
        if (isTable && _ballRb != null)
        {
            float  spd  = _ballRb.linearVelocity.magnitude;
            string side = nameLower.Contains("playerside") ? "PlayerSide" : "OpponentSide";

            // Fire OnTableBounce FIRST so ServeZoneClassifier stages the zone result
            // inside ExperimentLogger before LogBallLanding reads it.
            OnTableBounce?.Invoke(_curCol, _ballRb.position);

            ExperimentLogger.Instance?.LogBallLanding(side, spd, _ballRb.angularVelocity.magnitude);

            // OpponentSide landing ends the trial (logged after BallLanding row).
            if (side == "OpponentSide")
                ExperimentLogger.Instance?.LogTrialEnd();

            // Audio is intentionally NOT played here. OnCollisionEnter is the single
            // authoritative source for table/floor bounce sounds. This trigger path
            // is kept for logging, OnTableBounce, and spin transfer only.

            // ── Spin-to-velocity transfer at table contact ────────────────────
            //
            // Physics of the spin kick:
            //   Surface velocity of ball at contact = Cross(omega, r_contact)
            //   r_contact = (0, -BALL_RADIUS, 0)  (ball center → table)
            //
            //   Table friction opposes the surface motion → reaction on ball CM
            //   is in the OPPOSITE direction to the surface velocity:
            //     spinKick = -Cross(omega, (0, -R, 0)) × TABLE_FRICTION
            //
            // Result by component:
            //   Topspin  (omega.x > 0, rolling forward): +Z kick (ball kicks forward) ✓
            //   Backspin (omega.x < 0):                  -Z kick (ball checks / slows) ✓
            //   Sidespin (omega.z ≠ 0):                  ±X kick (lateral deviation)   ✓
            //
            // Y component is zeroed — don't fight the physics-engine vertical bounce.
            //
            Vector3 omega    = _ballRb.angularVelocity;
            Vector3 spinKick = -Vector3.Cross(omega, Vector3.down * BALL_RADIUS) * TABLE_FRICTION;
            spinKick.y = 0f;
            _ballRb.AddForce(spinKick, ForceMode.VelocityChange);

            // Spin decays on contact — some angular energy is absorbed by friction.
            _ballRb.angularVelocity = omega * SPIN_DECAY;
        }

        // ── Floor contact: audio only — ball bounces naturally ────────────────
        bool isFloor = nameLower == "floor" || nameLower.Contains("floor");
        if (isFloor && _ballRb != null)
        {
            float floorSpd = _ballRb.linearVelocity.magnitude;
            // Audio is intentionally NOT played here. OnCollisionEnter handles floor sound.
            ExperimentLogger.Instance?.LogBallLanding("Floor", floorSpd, _ballRb.angularVelocity.magnitude);
            ExperimentLogger.Instance?.LogTrialEnd();
        }

        // ── Net: audio only — no fault/scoring ───────────────────────────────
        bool isNet = _curCol == "Net" || nameLower.Contains("net");
        if (isNet)
        {
            PlaySound(_netHitClip, 3f, "NET");
            // Training mode: net contact plays a sound but does not end the rally.
            if (_ballRb != null)
                ExperimentLogger.Instance?.LogBallLanding(
                    "Net", _ballRb.linearVelocity.magnitude, _ballRb.angularVelocity.magnitude);
            return;
        }
    }

    // ── Audio ─────────────────────────────────────────────────────────────────
    void PlaySound(AudioClip clip, float ballSpeed, string surface = "")
    {
        if (_audio == null)  { Debug.LogWarning("[ballbounce] PlaySound: _audio is NULL"); return; }
        if (clip == null)    { Debug.LogWarning("[ballbounce] PlaySound: clip is NULL");   return; }

        // Strong cooldown prevents stacked sounds from collision + net/floor/table
        // events that can arrive in adjacent physics frames.
        if (Time.time - _lastBounceTime < 0.12f) return;
        _lastBounceTime = Time.time;

        float pitch  = Mathf.Clamp(0.85f + ballSpeed * 0.04f, 0.75f, 1.35f);
        float volume = Mathf.Clamp(0.45f + ballSpeed * 0.04f, 0.45f, 0.85f);

        // Do not use PlayOneShot here. Stop the previous clip so table/floor/net
        // bounces cannot overlap into a doubled impact sound.
        _audio.Stop();
        _audio.clip   = clip;
        _audio.pitch  = pitch;
        _audio.volume = volume;
        _audio.Play();

        Debug.Log($"[SOUND-DIAG] source=ballbounce.PlaySound  surface={surface}  spd={ballSpeed:F1}  " +
                  $"vol={volume:F2}  pitch={pitch:F2}  clip={clip.name}  frame={Time.frameCount}");
    }

}

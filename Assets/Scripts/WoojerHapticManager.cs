// WoojerHapticManager.cs
// Haptic feedback for the Multimodal condition (Group 3) ONLY.
//
// MECHANISM
//   The Woojer vest operates in audio-reactive mode.  Low-frequency audio clips
//   are played through hidden 2D AudioSources; the vest vibrates from audio, not
//   from SDK commands.  No SDK, no Bluetooth code, no native plugins.
//
// THREE HAPTIC EVENTS
//   1. Boundary violation  — looping low-freq rumble, volume ∝ depth
//   2. Serve zone failure  — short distinct buzz on ExperimentLogger.OnServeZoneResult(false)
//   3. Paddle impact       — brief tap, volume/pitch ∝ hit speed + spin
//
// EVENT ARCHITECTURE (unchanged)
//   ForwardBoundarySystem.OnBoundaryViolation  → HandleBoundaryViolation
//   gameplay.OnValidPaddleHit                  → HandleValidPaddleHit
//   ExperimentLogger.OnServeZoneResult         → HandleServeZoneResult
//   (ServeZoneClassifier evaluates zone geometry and publishes the result;
//    this manager never interprets bounces or zone colliders directly.)
//
// AUDIOSOURCE LAYOUT
//   Hidden child GameObject "WoojerHaptics" created in Awake.
//   Three AudioSources: _boundarySource (loop), _serveFailSource, _paddleImpactSource.
//   All: spatialBlend=0, playOnAwake=false, dopplerLevel=0.
//   Optionally routed to a dedicated AudioMixerGroup.
//
// PERFORMANCE (Quest-safe)
//   Zero allocations per frame.  No Update() polling — LateUpdate only.
//   No coroutines.  AudioSources reused.  Boundary uses looping clip with
//   volume lerp instead of repeated Play() calls.

using UnityEngine;
using UnityEngine.Audio;

[DisallowMultipleComponent]
public class WoojerHapticManager : MonoBehaviour
{
    // ── Inspector — Audio Clips ────────────────────────────────────────────────
    [Header("Audio Clips")]
    [Tooltip("Looping low-frequency rumble (~40–80 Hz) for boundary warning. " +
             "Assign a short loopable audio clip baked at very low frequency.")]
    public AudioClip boundaryClip;

    [Tooltip("Short distinct buzz (~0.2–0.3 s) for failed serve zone landing.")]
    public AudioClip serveFailClip;

    [Tooltip("Brief tap/thump for valid paddle contact. Natural decay preferred.")]
    public AudioClip paddleImpactClip;

    [Tooltip("Short thump when paddle bottom crosses below table surface. " +
             "Auto-generated at runtime (65 Hz, 0.20 s) if left unassigned.")]
    public AudioClip paddleTableClip;

    // ── Inspector — Volumes ────────────────────────────────────────────────────
    [Header("Volumes")]
    [Range(0f, 1f)]
    [Tooltip("Maximum volume for boundary rumble (intensity-modulated below this).")]
    public float boundaryVolume      = 0.60f;

    [Range(0f, 1f)]
    [Tooltip("Volume for the serve-zone failure buzz.")]
    public float serveFailVolume     = 0.85f;

    [Range(0f, 1f)]
    [Tooltip("Maximum volume for paddle impact (speed-modulated below this).")]
    public float paddleImpactVolume  = 0.70f;

    [Range(0f, 1f)]
    [Tooltip("Maximum volume for paddle-table contact thump (depth-modulated below this).")]
    public float paddleTableVolume   = 0.75f;

    // ── Inspector — Haptic Settings ────────────────────────────────────────────
    [Header("Haptic Settings")]
    [Tooltip("Master enable/disable. Turning off silences all AudioSources immediately.")]
    public bool enableHaptics = true;

    [Tooltip("The group number that receives haptic feedback. Default = 3 (Multimodal).")]
    public int  multimodalGroupNumber = 3;

    // ── Inspector — Mixer ──────────────────────────────────────────────────────
    [Header("Mixer (Optional)")]
    [Tooltip("Route all haptic AudioSources through a dedicated Mixer Group " +
             "(e.g., to separate them from gameplay audio in the mixer).")]
    public AudioMixerGroup hapticMixerGroup;

    // ── Inspector — Debug ──────────────────────────────────────────────────────
    [Header("Debug / Force Testing")]
    [Tooltip("Bypass group-number and session-active checks for editor testing. " +
             "MUST be OFF during data collection.")]
    public bool forceActiveForTesting = false;

    [Tooltip("Log haptic trigger events to the Unity console.")]
    public bool enableDebugLogs = false;

    // ── Inspector — Runtime Diagnostics (read-only, updated every frame) ───────
    [Header("Runtime Diagnostics (read-only)")]
    [SerializeField] private int    _diagGroup;
    [SerializeField] private bool   _diagSessionActive;
    [SerializeField] private bool   _diagHapticActive;
    [SerializeField] private string _diagCurrentClip   = "—";
    [SerializeField] private float  _diagCurrentVolume;

    // ── Runtime — AudioSources ─────────────────────────────────────────────────
    private AudioSource _boundarySource;
    private AudioSource _serveFailSource;
    private AudioSource _paddleImpactSource;
    private AudioSource _paddleTableSource;

    // ── Runtime — Boundary state ───────────────────────────────────────────────
    // _boundaryIntensity is written by the event handler and consumed in LateUpdate.
    // Reset at the end of every LateUpdate; re-written next frame if still in violation.
    private float _boundaryIntensity  = 0f;
    private bool  _boundaryEventLogged = false;  // one-shot log per violation onset

    // ── Condition gate ─────────────────────────────────────────────────────────
    private bool HapticActive =>
        enableHaptics &&
        (forceActiveForTesting ||
         (ExperimentLogger.SessionActive &&
          ExperimentLogger.GroupNumber == multimodalGroupNumber));

    // ── Awake — build AudioSources, generate procedural clips if needed ──────────

    void Awake()
    {
        var hapticGO = new GameObject("WoojerHaptics");
        hapticGO.transform.SetParent(transform, worldPositionStays: false);

        _boundarySource     = CreateSource(hapticGO, loop: true);
        _serveFailSource    = CreateSource(hapticGO, loop: false);
        _paddleImpactSource = CreateSource(hapticGO, loop: false);
        _paddleTableSource  = CreateSource(hapticGO, loop: false);

        GenerateProceduralClips();
    }

    AudioSource CreateSource(GameObject parent, bool loop)
    {
        var src = parent.AddComponent<AudioSource>();
        src.spatialBlend          = 0f;    // 2D — no positional audio
        src.playOnAwake           = false;
        src.loop                  = loop;
        src.dopplerLevel          = 0f;    // no pitch shift from head movement
        src.volume                = 0f;
        src.priority              = 0;     // highest priority — never culled when player moves
        src.bypassListenerEffects = true;  // raw signal to Woojer; no DSP filtering
        src.bypassReverbZones     = true;  // no reverb — prevents bass attenuation
        if (hapticMixerGroup != null)
            src.outputAudioMixerGroup = hapticMixerGroup;
        return src;
    }

    // ── Start — emit startup diagnostics after all Awake() calls complete ─────

    void Start()
    {
        Debug.Log(
            $"[WOOJER-DIAG] group={ExperimentLogger.GroupNumber}" +
            $" sessionActive={(ExperimentLogger.SessionActive ? "true" : "false")}" +
            $" hapticActive={(HapticActive ? "true" : "false")}"
        );
        Debug.Log($"[WOOJER-DIAG] boundaryClipAssigned={(boundaryClip          != null ? "true" : "false")}");
        Debug.Log($"[WOOJER-DIAG] serveFailClipAssigned={(serveFailClip        != null ? "true" : "false")}");
        Debug.Log($"[WOOJER-DIAG] paddleImpactClipAssigned={(paddleImpactClip  != null ? "true" : "false")}");
        Debug.Log($"[WOOJER-DIAG] paddleTableClipAssigned={(paddleTableClip    != null ? "true" : "false")}");
    }

    // ── Lifecycle — event wiring ───────────────────────────────────────────────

    void OnEnable()
    {
        ForwardBoundarySystem.OnBoundaryViolation += HandleBoundaryViolation;
        gameplay.OnValidPaddleHit                 += HandleValidPaddleHit;
        gameplay.OnPaddleTableContact             += HandlePaddleTableContact;
        ExperimentLogger.OnServeZoneResult        += HandleServeZoneResult;
    }

    void OnDisable()
    {
        ForwardBoundarySystem.OnBoundaryViolation -= HandleBoundaryViolation;
        gameplay.OnValidPaddleHit                 -= HandleValidPaddleHit;
        gameplay.OnPaddleTableContact             -= HandlePaddleTableContact;
        ExperimentLogger.OnServeZoneResult        -= HandleServeZoneResult;

        SilenceAll();
    }

    // ── Event handlers — store data; no audio calls inside ────────────────────
    // Audio is always driven from LateUpdate (boundary) or
    // called directly for one-shot events (serve fail, paddle).

    void HandleBoundaryViolation(float intensity)
    {
        if (!HapticActive) return;
        _boundaryIntensity = intensity;
    }

    void HandleServeZoneResult(bool success)
    {
        if (!HapticActive) return;
        if (success) return;   // no feedback for a correct serve

        if (serveFailClip == null) return;

        // One-shot buzz — stop any previous instance so it always
        // plays from the start (doubles are not a concern here;
        // ServeZoneClassifier guarantees one result per serve).
        _serveFailSource.clip   = serveFailClip;
        _serveFailSource.volume = serveFailVolume;
        _serveFailSource.pitch  = 1f;
        _serveFailSource.Play();

        ExperimentLogger.Instance?.LogHapticEvent("Woojer_ServeFail");
        Debug.Log("[WOOJER-EVENT] type=ServeFail");
    }

    void HandleValidPaddleHit(float effectiveSpd, float spinMag)
    {
        if (!HapticActive) return;
        if (paddleImpactClip == null) return;

        // Volume: InverseLerp maps 1 m/s → 0, 10 m/s → 1; small spin bonus.
        // Clamped to [0.2, 1.0] — always perceptible, never over-driven.
        float t   = Mathf.InverseLerp(1f, 10f, effectiveSpd);
        float vol = Mathf.Lerp(0.25f, 1f, t) + spinMag * 0.003f;
        vol       = Mathf.Clamp(vol, 0.2f, 1f) * paddleImpactVolume;

        // Pitch: slightly higher pitch on harder hits — compresses perceived
        // duration and adds a subtle sharpness without changing the clip.
        float pitch = Mathf.Lerp(0.85f, 1.15f, t);

        _paddleImpactSource.clip   = paddleImpactClip;
        _paddleImpactSource.volume = vol;
        _paddleImpactSource.pitch  = pitch;
        _paddleImpactSource.Play();

        ExperimentLogger.Instance?.LogHapticEvent("Woojer_PaddleImpact");
        Debug.Log($"[WOOJER-EVENT] type=PaddleImpact speed={effectiveSpd:F1} spin={spinMag:F0} vol={vol:F2}");
    }

    void HandlePaddleTableContact(float depth)
    {
        // Volume: even a light touch (depth→0) gives 0.75; full penetration (≥0.01 m) gives 1.0.
        float vol = Mathf.Lerp(0.75f, 1.0f, Mathf.Clamp01(depth / 0.01f)) * paddleTableVolume;

        // Log BEFORE any early return so the chain is always visible in adb logcat.
        Debug.Log(
            $"[PADDLE-TABLE-HAPTIC] receiver=Woojer" +
            $" depth={depth:F4}" +
            $" active={HapticActive}" +
            $" group={ExperimentLogger.GroupNumber}" +
            $" session={ExperimentLogger.SessionActive}" +
            $" volume={vol:F2}" +
            $" clipAssigned={(paddleTableClip != null ? "true" : "false")}"
        );

        if (!HapticActive) return;
        if (paddleTableClip == null) return;

        _paddleTableSource.clip   = paddleTableClip;
        _paddleTableSource.volume = vol;
        _paddleTableSource.pitch  = 1f;
        _paddleTableSource.Play();

        ExperimentLogger.Instance?.LogHapticEvent("Woojer_PaddleTable");
    }

    // ── LateUpdate — boundary looping clip ────────────────────────────────────
    // Runs after all Update() calls so HandleBoundaryViolation has already
    // stored the frame's intensity before we act on it.

    void LateUpdate()
    {
        // ── Update inspector diagnostics every frame ───────────────────────────
        _diagGroup         = ExperimentLogger.GroupNumber;
        _diagSessionActive = ExperimentLogger.SessionActive;
        _diagHapticActive  = HapticActive;

        if (_boundarySource.isPlaying)
        {
            _diagCurrentClip   = boundaryClip     != null ? boundaryClip.name     : "—";
            _diagCurrentVolume = _boundarySource.volume;
        }
        else if (_serveFailSource.isPlaying)
        {
            _diagCurrentClip   = serveFailClip    != null ? serveFailClip.name    : "—";
            _diagCurrentVolume = _serveFailSource.volume;
        }
        else if (_paddleImpactSource.isPlaying)
        {
            _diagCurrentClip   = paddleImpactClip != null ? paddleImpactClip.name : "—";
            _diagCurrentVolume = _paddleImpactSource.volume;
        }
        else if (_paddleTableSource.isPlaying)
        {
            _diagCurrentClip   = paddleTableClip != null ? paddleTableClip.name : "—";
            _diagCurrentVolume = _paddleTableSource.volume;
        }
        else
        {
            _diagCurrentClip   = "—";
            _diagCurrentVolume = 0f;
        }

        if (!HapticActive)
        {
            SilenceAll();
            _boundaryIntensity    = 0f;
            _boundaryEventLogged  = false;
            return;
        }

        if (_boundaryIntensity > 0f && boundaryClip != null)
        {
            float targetVol = _boundaryIntensity * boundaryVolume;

            if (!_boundarySource.isPlaying)
            {
                _boundarySource.clip   = boundaryClip;
                _boundarySource.volume = 0f;   // fade in from silence
                _boundarySource.pitch  = 1f;
                _boundarySource.Play();
            }

            // Log once per violation onset (not every frame)
            if (!_boundaryEventLogged)
            {
                _boundaryEventLogged = true;
                Debug.Log($"[WOOJER-EVENT] type=Boundary intensity={_boundaryIntensity:F2}");
            }

            // Smooth volume ramp: rise fast, fall fast — avoids clicks.
            _boundarySource.volume = Mathf.MoveTowards(
                _boundarySource.volume, targetVol, Time.deltaTime * 5f);
        }
        else
        {
            // Violation ended — fade out then stop to avoid click on cut.
            if (_boundarySource.isPlaying)
            {
                _boundarySource.volume = Mathf.MoveTowards(
                    _boundarySource.volume, 0f, Time.deltaTime * 10f);

                if (_boundarySource.volume <= 0f)
                    _boundarySource.Stop();
            }
            _boundaryEventLogged = false;   // reset so next violation logs again
        }

        // Reset here — re-written by event handler next frame if still active.
        _boundaryIntensity = 0f;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void SilenceAll()
    {
        if (_boundarySource     != null && _boundarySource.isPlaying)     _boundarySource.Stop();
        if (_serveFailSource    != null && _serveFailSource.isPlaying)    _serveFailSource.Stop();
        if (_paddleImpactSource != null && _paddleImpactSource.isPlaying) _paddleImpactSource.Stop();
        if (_paddleTableSource  != null && _paddleTableSource.isPlaying)  _paddleTableSource.Stop();
    }

    // ── Procedural clip generation ────────────────────────────────────────────
    // Fills any unassigned clip slot with a synthesised sine-wave clip.
    // Inspector-assigned clips are never overwritten.

    private enum ClipEnvelope { Flat, FadeInOut, ExpDecay }

    void GenerateProceduralClips()
    {
        int rate = AudioSettings.outputSampleRate;

        // 1. Boundary — 50 Hz, 2 s, flat (loops seamlessly)
        if (boundaryClip == null)
            boundaryClip = BuildSineClip("WoojerBoundary", 50f, 2f, rate, ClipEnvelope.Flat);

        // 2. Serve fail — 70 Hz, 0.25 s, symmetric fade-in/out envelope
        if (serveFailClip == null)
            serveFailClip = BuildSineClip("WoojerServeFail", 70f, 0.25f, rate, ClipEnvelope.FadeInOut);

        // 3. Paddle impact — 85 Hz, 0.15 s, exponential decay
        if (paddleImpactClip == null)
            paddleImpactClip = BuildSineClip("WoojerPaddleImpact", 85f, 0.15f, rate, ClipEnvelope.ExpDecay);

        // 4. Paddle-table contact — 65 Hz, 0.20 s, exponential decay
        if (paddleTableClip == null)
            paddleTableClip = BuildSineClip("WoojerPaddleTable", 65f, 0.20f, rate, ClipEnvelope.ExpDecay);

        Debug.Log("[WOOJER-DIAG] Generated procedural haptic clips.");
    }

    static AudioClip BuildSineClip(string clipName, float freq, float durationSec,
                                    int sampleRate, ClipEnvelope env)
    {
        int     n    = Mathf.Max(2, Mathf.RoundToInt(durationSec * sampleRate));
        float[] data = new float[n];

        for (int i = 0; i < n; i++)
        {
            float t        = (float)i / sampleRate;
            float raw      = Mathf.Sin(2f * Mathf.PI * freq * t);
            float progress = (float)i / (n - 1);
            float envelope;

            switch (env)
            {
                case ClipEnvelope.FadeInOut:
                    if      (progress < 0.2f) envelope = progress / 0.2f;
                    else if (progress > 0.8f) envelope = (1f - progress) / 0.2f;
                    else                      envelope = 1f;
                    break;
                case ClipEnvelope.ExpDecay:
                    envelope = Mathf.Exp(-6f * progress);
                    break;
                default: // Flat
                    envelope = 1f;
                    break;
            }

            data[i] = raw * envelope;
        }

        var clip = AudioClip.Create(clipName, n, channels: 1,
                                    frequency: sampleRate, stream: false);
        clip.SetData(data, 0);
        return clip;
    }
}

using UnityEngine;

/// <summary>
/// Audio-only paddle hit detector.
/// Does NOT modify ball physics, paddle physics, collisions, velocities, or gameplay state.
/// Attach this to the moving paddle GameObject. Assign Ball and, preferably, Paddle Box.
/// </summary>
public class PaddleHitAudioOnly_FIXED : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The BoxCollider on the paddle/rubber face. If empty, the script tries GetComponent<BoxCollider>().")]
    public BoxCollider paddleBox;

    [Tooltip("The table tennis ball GameObject.")]
    public GameObject ball;

    [Header("Detection")]
    [Tooltip("Distance from ball centre to paddle collider closest point that counts as contact. 0.04–0.05 usually works for a table tennis ball.")]
    public float hitDistance = 0.045f;

    [Tooltip("Minimum time between click sounds.")]
    public float cooldown = 0.08f;

    [Tooltip("Minimum combined ball + paddle speed required before playing a hit sound.")]
    public float minRelativeSpeed = 0.25f;

    [Header("Audio")]
    [Range(0f, 1f)] public float volume = 1f;
    public bool logDiagnostics = true;

    private Rigidbody ballRb;
    private AudioSource audioSource;
    private AudioClip clickClip;

    private Vector3 previousPaddlePos;
    private float previousDistance = float.PositiveInfinity;
    private float lastSoundTime = -10f;

    void Start()
    {
        if (paddleBox == null)
            paddleBox = GetComponent<BoxCollider>();

        if (ball != null)
            ballRb = ball.GetComponent<Rigidbody>();

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f; // reliable 2D sound on Quest
        audioSource.volume = volume;
        audioSource.mute = false;
        audioSource.enabled = true;
        audioSource.priority = 0;
        audioSource.outputAudioMixerGroup = null;
        audioSource.bypassEffects = true;
        audioSource.bypassListenerEffects = true;
        audioSource.bypassReverbZones = true;

        clickClip = CreatePaddleClickClip();
        previousPaddlePos = transform.position;

        if (logDiagnostics)
        {
            Debug.Log($"[PADDLE-AUDIO-ONLY] initialized " +
                      $"paddleBoxNull={paddleBox == null} " +
                      $"ballNull={ball == null} " +
                      $"ballRbNull={ballRb == null} " +
                      $"attachedTo={gameObject.name}");
        }
    }

    void FixedUpdate()
    {
        if (paddleBox == null || ball == null || ballRb == null || clickClip == null || audioSource == null)
            return;

        Vector3 ballPos = ballRb.position;
        Vector3 closest = paddleBox.ClosestPoint(ballPos);
        float distance = Vector3.Distance(ballPos, closest);

        Vector3 paddleVelocity = (transform.position - previousPaddlePos) / Mathf.Max(Time.fixedDeltaTime, 0.0001f);
        float relativeSpeed = paddleVelocity.magnitude + ballRb.linearVelocity.magnitude;

        bool enteredContactZone = previousDistance > hitDistance && distance <= hitDistance;
        bool fastEnough = relativeSpeed >= minRelativeSpeed;
        bool cooledDown = Time.time >= lastSoundTime + cooldown;
        bool ballActive = !ballRb.isKinematic;

        if (enteredContactZone && fastEnough && cooledDown && ballActive)
        {
            PlayClick(relativeSpeed, distance);
        }

        previousDistance = distance;
        previousPaddlePos = transform.position;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (ball == null || Time.time < lastSoundTime + cooldown)
            return;

        bool isBall = collision.gameObject == ball || collision.transform.IsChildOf(ball.transform);
        if (!isBall)
            return;

        float speed = ballRb != null ? ballRb.linearVelocity.magnitude : 1f;
        PlayClick(speed, 0f);
    }

    private void PlayClick(float speed, float distance)
    {
        audioSource.volume = volume;
        audioSource.pitch = Mathf.Clamp(0.95f + speed * 0.03f, 0.9f, 1.25f);
        audioSource.PlayOneShot(clickClip, 1f);
        lastSoundTime = Time.time;

        if (logDiagnostics)
        {
            Debug.Log($"[PADDLE-AUDIO-ONLY] sound played " +
                      $"speed={speed:F3} distance={distance:F4} " +
                      $"frame={Time.frameCount}");
        }
    }

    [ContextMenu("Test Paddle Audio")]
    public void TestPaddleAudio()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();

        if (clickClip == null)
            clickClip = CreatePaddleClickClip();

        audioSource.spatialBlend = 0f;
        audioSource.volume = volume;
        audioSource.PlayOneShot(clickClip, 1f);
        Debug.Log("[PADDLE-AUDIO-ONLY] TestPaddleAudio played");
    }

    static AudioClip CreatePaddleClickClip()
    {
        const int sampleRate = 44100;
        const float duration = 0.09f;

        int samples = Mathf.RoundToInt(sampleRate * duration);
        float[] data = new float[samples];

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float env = Mathf.Exp(-t * 45f);

            float sig =
                0.55f * Mathf.Sin(2f * Mathf.PI * 1800f * t) +
                0.30f * Mathf.Sin(2f * Mathf.PI * 3200f * t) +
                0.15f * Mathf.Sin(2f * Mathf.PI * 5200f * t);

            data[i] = Mathf.Clamp(env * sig * 1.8f, -1f, 1f);
        }

        AudioClip clip = AudioClip.Create("PaddleClick_AudioOnly_FIXED", samples, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}

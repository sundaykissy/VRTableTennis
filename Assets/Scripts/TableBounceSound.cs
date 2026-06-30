using UnityEngine;

// Attach this component to the table surface collider GameObject
// (e.g. "playerside" or "enemyside", NOT the root "PingPondTable").
//
// Assign an AudioSource and a short bounce AudioClip in the Inspector.
// If no clip is assigned, a procedural ping is generated automatically.
// Volume is scaled by impact speed; a small random pitch shift adds variety.
//
// The ball is detected by tag ("Ball") OR by GameObject name ("Ball") —
// no tag setup is required as long as the ball is named "Ball" in the hierarchy.
public class TableBounceSound : MonoBehaviour
{
    public AudioSource audioSource;
    public AudioClip   bounceClip;

    private AudioClip _generatedClip;

    void Awake()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.spatialBlend = 0.3f;
        audioSource.playOnAwake  = false;

        if (bounceClip == null)
            bounceClip = _generatedClip = GeneratePing(860f, 0.05f);
    }

    void OnCollisionEnter(Collision col)
    {
        if (audioSource == null || bounceClip == null) return;

        bool isBall = col.gameObject.CompareTag("Ball")
                   || col.gameObject.name == "Ball";
        if (!isBall) return;

        float v = col.relativeVelocity.magnitude;
        audioSource.pitch = Random.Range(0.93f, 1.07f);
        audioSource.PlayOneShot(bounceClip, Mathf.Clamp(v * 0.06f, 0.25f, 1f));
    }

    static AudioClip GeneratePing(float hz, float dur)
    {
        const int rate = 44100;
        int samples = Mathf.RoundToInt(rate * dur);
        float[] data = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / rate;
            data[i] = Mathf.Exp(-t * 40f) * Mathf.Sin(2f * Mathf.PI * hz * t);
        }
        var clip = AudioClip.Create("TablePing", samples, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}

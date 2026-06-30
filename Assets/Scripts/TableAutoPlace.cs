using UnityEngine;
using UnityEngine.XR;
using System.Collections;
using System.Collections.Generic;
#pragma warning disable CS0618

public class TableAutoPlace : MonoBehaviour
{
    [Header("Placement")]
    public float playerEdgeDistance = 0.90f;
    public float tableHeight = 0.76f;
    public float playerEyeHeight = 1.55f;

    [Header("Startup Recenter")]
    public bool recenterOnStart = true;
    public float startupRecenterDelay = 1.0f;

    private readonly List<InputDevice> _devices = new();
    private readonly List<XRInputSubsystem> _inputSubsystems = new();

    private bool _menuWasPressed;
    private bool _subscribed;

    void OnEnable()
    {
        TrySubscribe();
    }

    void Start()
    {
        if (recenterOnStart)
            StartCoroutine(RecenterAfterStartup());
    }

    void OnDisable()
    {
        foreach (var sub in _inputSubsystems)
            sub.trackingOriginUpdated -= OnTrackingOriginUpdated;
    }

    void Update()
    {
        if (!_subscribed)
            TrySubscribe();

        _devices.Clear();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller, _devices);

        bool menuPressed = false;

        foreach (var dev in _devices)
        {
            if (dev.TryGetFeatureValue(CommonUsages.menuButton, out bool pressed) && pressed)
            {
                menuPressed = true;
                break;
            }
        }

        if (menuPressed && !_menuWasPressed)
            PlaceTable("MenuButton");

        _menuWasPressed = menuPressed;
    }

    void TrySubscribe()
    {
        SubsystemManager.GetInstances(_inputSubsystems);

        if (_inputSubsystems.Count == 0)
            return;

        foreach (var sub in _inputSubsystems)
        {
            sub.trackingOriginUpdated -= OnTrackingOriginUpdated;
            sub.trackingOriginUpdated += OnTrackingOriginUpdated;
        }

        _subscribed = true;
        Debug.Log("[TABLE-RECENTER] Subscribed to XR trackingOriginUpdated.");
    }

    void OnTrackingOriginUpdated(XRInputSubsystem sub)
    {
        StartCoroutine(RepositionNextFrame("OSRecenter"));
    }

    IEnumerator RecenterAfterStartup()
    {
        yield return new WaitForSeconds(startupRecenterDelay);
        yield return null;

        PlaceTable("Startup");
    }

    IEnumerator RepositionNextFrame(string source)
    {
        yield return null;
        PlaceTable(source);
    }

    void PlaceTable(string source)
    {
        Camera head = Camera.main;

        if (head == null)
        {
            Debug.LogWarning("[TABLE-RECENTER] Camera.main missing.");
            return;
        }

        Vector3 forward = head.transform.forward;
        forward.y = 0f;

        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;

        forward.Normalize();

        if (!TryGetTableGeometry(out Vector3 localOpponentAxis, out Vector3 localPlayerEdgeCenter))
        {
            Debug.LogWarning("[TABLE-RECENTER] Could not detect playerside/enemyside geometry.");
            return;
        }

        Vector3 currentWorldAxis = transform.TransformDirection(localOpponentAxis).normalized;
        Quaternion targetRot = Quaternion.FromToRotation(currentWorldAxis, forward) * transform.rotation;

        Vector3 desiredEdgeWorld = head.transform.position + forward * playerEdgeDistance;
        Vector3 targetPos = desiredEdgeWorld - (targetRot * localPlayerEdgeCenter);

        float floorY = head.transform.position.y - playerEyeHeight;
        targetPos.y = floorY + tableHeight;

        transform.SetPositionAndRotation(targetPos, targetRot);

        ForwardBoundarySystem boundary = FindFirstObjectByType<ForwardBoundarySystem>();
        if (boundary != null)
            boundary.RefreshFromTable();

        Debug.Log(
            $"[TABLE-RECENTER] source={source} " +
            $"forward={forward:F3} " +
            $"localOpponentAxis={localOpponentAxis:F3} " +
            $"localPlayerEdgeCenter={localPlayerEdgeCenter:F3} " +
            $"tablePos={transform.position:F3} " +
            $"tableRot={transform.rotation.eulerAngles:F1}"
        );
    }

    bool TryGetTableGeometry(out Vector3 localOpponentAxis, out Vector3 localPlayerEdgeCenter)
    {
        localOpponentAxis = Vector3.forward;
        localPlayerEdgeCenter = Vector3.zero;

        Transform playerSide = FindChildOrGlobal("playerside");
        Transform enemySide = FindChildOrGlobal("enemyside");

        if (playerSide == null || enemySide == null)
            return false;

        Vector3 playerLocal = transform.InverseTransformPoint(playerSide.position);
        Vector3 enemyLocal = transform.InverseTransformPoint(enemySide.position);

        localOpponentAxis = enemyLocal - playerLocal;
        localOpponentAxis.y = 0f;

        if (localOpponentAxis.sqrMagnitude < 0.001f)
            return false;

        localOpponentAxis.Normalize();

        Collider col = playerSide.GetComponent<Collider>();

        if (col == null)
        {
            localPlayerEdgeCenter = playerLocal;
            return true;
        }

        List<Vector3> localCorners = GetColliderCornersLocal(col);

        float minS = float.PositiveInfinity;

        foreach (Vector3 p in localCorners)
        {
            float s = Vector3.Dot(p, localOpponentAxis);
            if (s < minS)
                minS = s;
        }

        Vector3 sum = Vector3.zero;
        int count = 0;
        const float EPS = 0.01f;

        foreach (Vector3 p in localCorners)
        {
            float s = Vector3.Dot(p, localOpponentAxis);

            if (Mathf.Abs(s - minS) <= EPS)
            {
                sum += p;
                count++;
            }
        }

        localPlayerEdgeCenter = count > 0 ? sum / count : playerLocal;
        return true;
    }

    Transform FindChildOrGlobal(string name)
    {
        Transform t = transform.Find(name);
        if (t != null)
            return t;

        GameObject go = GameObject.Find(name);
        return go != null ? go.transform : null;
    }

    List<Vector3> GetColliderCornersLocal(Collider col)
    {
        List<Vector3> result = new();

        if (col is BoxCollider box)
        {
            Vector3 c = box.center;
            Vector3 h = box.size * 0.5f;

            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 localBoxPoint = c + new Vector3(h.x * x, h.y * y, h.z * z);
                Vector3 worldPoint = box.transform.TransformPoint(localBoxPoint);
                result.Add(transform.InverseTransformPoint(worldPoint));
            }
        }
        else
        {
            Bounds b = col.bounds;
            Vector3 min = b.min;
            Vector3 max = b.max;

            Vector3[] corners =
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z)
            };

            foreach (Vector3 worldPoint in corners)
                result.Add(transform.InverseTransformPoint(worldPoint));
        }

        return result;
    }
}
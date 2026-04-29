using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;
#pragma warning disable CS0618

public class VRCameraBootstrap : MonoBehaviour
{
    private static VRCameraBootstrap _manager;
    private List<InputDevice>        _devices       = new List<InputDevice>();
    private List<XRInputSubsystem>   _inputSubsystems = new List<XRInputSubsystem>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateManager()
    {
        if (_manager != null) return;
        GameObject go = new GameObject("VRCameraManager");
        _manager = go.AddComponent<VRCameraBootstrap>();
        DontDestroyOnLoad(go);
    }

    void Start()
    {
        // Ensure the XR Input Subsystem is running — without this,
        // XRNode.RightHand / LeftHand return (0,0,0) on PC via Link.
        SubsystemManager.GetInstances(_inputSubsystems);
        foreach (var s in _inputSubsystems)
            if (!s.running) s.Start();
    }

    void Update()
    {
        // Re-check subsystems every frame until at least one is running
        bool anySub = false;
        foreach (var s in _inputSubsystems) if (s.running) { anySub = true; break; }
        if (!anySub)
        {
            SubsystemManager.GetInstances(_inputSubsystems);
            foreach (var s in _inputSubsystems)
                if (!s.running) s.Start();
        }

        Camera cam = Camera.main;
        if (cam == null)
        {
            GameObject camGO = new GameObject("VR Main Camera");
            camGO.tag = "MainCamera";
            cam = camGO.AddComponent<Camera>();
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane  = 1000f;
        }

        bool tracked = false;

        // ── Try InputDevices (Android standalone) ──────────────────────────
        _devices.Clear();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.HeadMounted, _devices);
        if (_devices.Count > 0)
        {
            InputDevice hmd = _devices[0];
            bool gp = hmd.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 pos);
            bool gr = hmd.TryGetFeatureValue(CommonUsages.centerEyeRotation, out Quaternion rot);
            if (gp && gr && pos != Vector3.zero)
            {
                cam.transform.position = pos;
                cam.transform.rotation = rot;
                tracked = true;
            }
        }

        // ── Fall back to XRNode (PC via Oculus Link / OpenXR) ──────────────
        if (!tracked)
        {
            Vector3    nodePos = InputTracking.GetLocalPosition(XRNode.CenterEye);
            Quaternion nodeRot = InputTracking.GetLocalRotation(XRNode.CenterEye);
            if (nodePos.sqrMagnitude > 0.0001f || nodeRot != Quaternion.identity)
            {
                cam.transform.position = nodePos;
                cam.transform.rotation = nodeRot;
            }
        }
    }
}

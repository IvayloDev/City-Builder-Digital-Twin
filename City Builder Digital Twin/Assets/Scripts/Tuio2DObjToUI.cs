using System;
using System.Collections.Generic;
using UnityEngine;
using extOSC;
using extOSC.Core;

public class Tuio2DObjToUI : MonoBehaviour
{
    [Serializable]
    public class Mapping
    {
        public int classId;              // fiducial ID (e.g. 33)
        public RectTransform target;     // UI element to move
        public bool rotate;
    }

    [Header("OSC")]
    [SerializeField] private OSCReceiver receiver;

    [Header("UI Mapping")]
    [SerializeField] private RectTransform area;   // the UI rectangle representing the surface
    [SerializeField] private List<Mapping> mappings = new();

    [Header("Motion")]
    [SerializeField] private bool smooth = true;
    [SerializeField] private float smoothTime = 0.05f;

    private readonly Dictionary<int, Mapping> _byClassId = new();
    private readonly Dictionary<int, int> _sessionToClass = new(); // sessionId -> classId
    private readonly Dictionary<RectTransform, Vector2> _vel = new();

    private IOSCBind _tuioBind;
    
    private void Reset()
    {
        receiver = GetComponent<OSCReceiver>();
    }

    private void Awake()
    {
        foreach (var m in mappings)
        {
            if (m == null || m.target == null) continue;
            _byClassId[m.classId] = m;
        }
    }

    private void OnEnable()
    {
        if (receiver == null)
            receiver = GetComponent<OSCReceiver>();

        _tuioBind = receiver.Bind("/tuio/2Dobj", OnTuio2DObj);
    }

    private void OnDisable()
    {
        receiver.Unbind(_tuioBind);
    }

    private void OnTuio2DObj(OSCMessage msg)
    {
        if (msg.Values.Count < 1) return;
        if (msg.Values[0].Type != OSCValueType.String) return;

        var cmd = msg.Values[0].StringValue;

        switch (cmd)
        {
            case "set":
                HandleSet(msg);
                break;

            case "alive":
                HandleAlive(msg);
                break;

            case "fseq":
                // ignore, unless you want debugging
                break;
        }
    }

    private void HandleSet(OSCMessage msg)
    {
        // Expect: set, sessionId, classId, x, y, angle, ...
        if (msg.Values.Count < 6) return;

        int sessionId = msg.Values[1].IntValue;
        int classId   = msg.Values[2].IntValue;

        float xNorm   = msg.Values[3].FloatValue; // 0..1
        float yNorm   = msg.Values[4].FloatValue; // 0..1
        float angleRad= msg.Values[5].FloatValue;

        _sessionToClass[sessionId] = classId;

        if (!_byClassId.TryGetValue(classId, out var map) || map.target == null || area == null)
            return;

        // Convert normalized TUIO coords to anchoredPosition inside "area"
        var size = area.rect.size;

        // TUIO y grows downward; UI anchoredPosition grows upward.
        float x = (xNorm - 0.5f) * size.x;
        float y = (0.5f - yNorm) * size.y;

        Vector2 targetPos = new Vector2(x, y);

        if (smooth)
        {
            if (!_vel.TryGetValue(map.target, out var v)) v = Vector2.zero;
            map.target.anchoredPosition = Vector2.SmoothDamp(map.target.anchoredPosition, targetPos, ref v, smoothTime);
            _vel[map.target] = v;
        }
        else
        {
            map.target.anchoredPosition = targetPos;
        }

        if (map.rotate)
        {
            float angleDeg = angleRad * Mathf.Rad2Deg;
            map.target.localRotation = Quaternion.Euler(0, 0, -angleDeg);
        }

        // Optional: ensure visible when tracked
        if (!map.target.gameObject.activeSelf)
            map.target.gameObject.SetActive(true);
    }

    private void HandleAlive(OSCMessage msg)
    {
        // alive, sessionId1, sessionId2, ...
        // Hide UI objects that are no longer alive (optional)
        var aliveSessions = new HashSet<int>();
        for (int i = 1; i < msg.Values.Count; i++)
        {
            if (msg.Values[i].Type == OSCValueType.Int)
                aliveSessions.Add(msg.Values[i].IntValue);
        }

        // Find which classIds are still alive
        var aliveClassIds = new HashSet<int>();
        foreach (var kv in _sessionToClass)
        {
            if (aliveSessions.Contains(kv.Key))
                aliveClassIds.Add(kv.Value);
        }

        // Hide unmapped or dead objects
        foreach (var kv in _byClassId)
        {
            var map = kv.Value;
            if (map?.target == null) continue;

            bool isAlive = aliveClassIds.Contains(kv.Key);
            // Only auto-hide if you want that behavior:
            // map.target.gameObject.SetActive(isAlive);
        }
    }
}
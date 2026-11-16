using NOT_Lonely;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// 3D sound for the NOT_Lonely overhead crane, driven only by its state (no changes to the crane asset):
/// - bridge travel loop at both end-beam wheel trucks, trolley loop at the trolley, hoist loop at the hoist;
///   each fades and pitches with its axis speed (hoist slightly higher going up than down);
/// - motor start (contactor + brake release) and stop (brake clamp) one-shots per axis;
/// - an end-of-travel clank when the bridge, trolley or hook reaches its limit while moving;
/// - an optional travel warning beeper while the bridge or trolley moves.
/// Works with every control path (remote, keyboard, desktop) because it watches initialBeamPos,
/// initialTelpherPos and hookHeight. Put it on the OverheadCrane. Clips live in Assets/Audio/Crane.
/// </summary>
[DisallowMultipleComponent]
public class OverheadCraneAudio : MonoBehaviour
{
    [Tooltip("The crane to listen to. Found on this GameObject when empty.")]
    public NL_OverheadCrane crane;

    [Tooltip("Optional mixer group for every crane sound.")]
    public AudioMixerGroup output;

    [Header("Clips")]
    public AudioClip bridgeLoop;
    public AudioClip trolleyLoop;
    public AudioClip hoistLoop;
    public AudioClip motorStart;
    public AudioClip motorStop;
    public AudioClip limitHit;
    public AudioClip warningBeepLoop;

    [Header("Levels")]
    [Range(0f, 1f)] public float masterVolume = 1f;
    [Range(0f, 1f)] public float bridgeVolume = 0.8f;
    [Range(0f, 1f)] public float trolleyVolume = 0.6f;
    [Range(0f, 1f)] public float hoistVolume = 0.7f;
    [Range(0f, 1f)] public float startStopVolume = 0.6f;
    [Range(0f, 1f)] public float limitVolume = 0.8f;
    [Range(0f, 1f)] public float warningVolume = 0.35f;

    [Header("Behaviour")]
    [Tooltip("Play the warning beeper while the bridge or trolley travels.")]
    public bool travelWarningBeeper = false;
    [Tooltip("Seconds for a motor loop to spool up / down.")]
    public float spoolUpTime = 0.2f;
    public float spoolDownTime = 0.45f;
    [Tooltip("An axis counts as stopped after this many seconds without movement (the remote moves it frame by frame).")]
    public float stopDelay = 0.12f;

    [Header("3D")]
    public float minDistance = 1.5f;
    public float maxDistance = 30f;

    private sealed class Axis
    {
        public string name;
        public AudioSource loopA, loopB;   // B only for the bridge (second end truck)
        public Transform at, atB;
        public float last;                 // 0..1 position last frame
        public float level;                // spooled 0..1
        public float idleFor = 999f;
        public bool running;
        public float dir;
        public float baseVolume;
    }

    private Axis _bridge, _trolley, _hoist;
    private AudioSource[] _oneShots;
    private int _next;
    private AudioSource _beeper;
    private bool _primed;

    private void Awake()
    {
        if (crane == null)
            crane = GetComponent<NL_OverheadCrane>();
        if (crane == null)
        {
            enabled = false;
            return;
        }

        Transform root = crane.transform;
        Transform endBeam1 = Find(root, "EndBeam_01");
        Transform endBeam2 = Find(root, "EndBeam_02");
        Transform trolley = Find(root, "TelpherParent");
        Transform hoist = Find(root, "Telpher") ?? trolley;

        _bridge = MakeAxis("Bridge", bridgeLoop, endBeam1 ?? Find(root, "MovingParts") ?? root, endBeam2, bridgeVolume);
        _trolley = MakeAxis("Trolley", trolleyLoop, trolley ?? root, null, trolleyVolume);
        _hoist = MakeAxis("Hoist", hoistLoop, hoist ?? root, null, hoistVolume);

        var shots = new GameObject("CraneAudio_OneShots").transform;
        shots.SetParent(transform, false);
        _oneShots = new AudioSource[6];
        for (int i = 0; i < _oneShots.Length; i++)
        {
            var go = new GameObject($"Voice_{i}");
            go.transform.SetParent(shots, false);
            _oneShots[i] = MakeSource(go, false);
        }

        _beeper = MakeSource(new GameObject("CraneAudio_Beeper"), true);
        _beeper.transform.SetParent(trolley != null ? trolley : root, false);
        _beeper.clip = warningBeepLoop;
    }

    private Axis MakeAxis(string name, AudioClip clip, Transform at, Transform atB, float volume)
    {
        var a = new Axis { name = name, at = at, atB = atB, baseVolume = volume };
        a.loopA = MakeSource(new GameObject($"CraneAudio_{name}"), true);
        a.loopA.transform.SetParent(at, false);
        a.loopA.clip = clip;
        if (atB != null)
        {
            a.loopB = MakeSource(new GameObject($"CraneAudio_{name}_B"), true);
            a.loopB.transform.SetParent(atB, false);
            a.loopB.clip = clip;
        }
        return a;
    }

    private AudioSource MakeSource(GameObject go, bool loop)
    {
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 1f;
        s.rolloffMode = AudioRolloffMode.Logarithmic;
        s.minDistance = minDistance;
        s.maxDistance = maxDistance;
        s.dopplerLevel = 0f;
        s.volume = 0f;
        s.outputAudioMixerGroup = output;
        return s;
    }

    private static Transform Find(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name)
                return t;
        return null;
    }

    private void OnDisable()
    {
        foreach (var a in new[] { _bridge, _trolley, _hoist })
        {
            if (a == null) continue;
            if (a.loopA != null) a.loopA.Stop();
            if (a.loopB != null) a.loopB.Stop();
            a.level = 0f;
            a.running = false;
        }
        if (_beeper != null) _beeper.Stop();
    }

    private void LateUpdate()
    {
        if (crane == null)
            return;

        if (!_primed)
        {
            _bridge.last = crane.initialBeamPos;
            _trolley.last = crane.initialTelpherPos;
            _hoist.last = crane.hookHeight;
            _primed = true;
            return;
        }

        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        UpdateAxis(_bridge, crane.initialBeamPos, dt, 1f);
        UpdateAxis(_trolley, crane.initialTelpherPos, dt, 1f);
        UpdateAxis(_hoist, crane.hookHeight, dt, _hoist.dir > 0f ? 1.05f : 0.95f);

        bool travelling = _bridge.running || _trolley.running;
        if (travelWarningBeeper && warningBeepLoop != null && travelling)
        {
            _beeper.volume = warningVolume * masterVolume;
            if (!_beeper.isPlaying) _beeper.Play();
        }
        else if (_beeper.isPlaying)
        {
            _beeper.Stop();
        }
    }

    private void UpdateAxis(Axis a, float pos, float dt, float pitchBias)
    {
        float delta = pos - a.last;
        a.last = pos;
        bool moving = Mathf.Abs(delta) > 1e-6f;

        if (moving)
        {
            a.dir = Mathf.Sign(delta);
            a.idleFor = 0f;
            if (!a.running)
            {
                a.running = true;
                PlayAt(motorStart, a.at.position, startStopVolume, Random.Range(0.96f, 1.04f));
            }
            // Reached the end of travel while driving into it.
            if ((pos >= 0.9995f && a.dir > 0f) || (pos <= 0.0005f && a.dir < 0f))
                PlayLimit(a);
        }
        else
        {
            a.idleFor += dt;
            if (a.running && a.idleFor >= stopDelay)
            {
                a.running = false;
                PlayAt(motorStop, a.at.position, startStopVolume, Random.Range(0.96f, 1.04f));
            }
        }

        float target = a.running ? 1f : 0f;
        float rate = 1f / Mathf.Max(0.01f, target > a.level ? spoolUpTime : spoolDownTime);
        a.level = Mathf.MoveTowards(a.level, target, rate * dt);

        float vol = a.level * a.baseVolume * masterVolume * (a.loopB != null ? 0.7f : 1f);
        float pitch = (0.7f + 0.3f * a.level) * pitchBias;
        DriveLoop(a.loopA, vol, pitch);
        DriveLoop(a.loopB, vol, pitch * 0.98f);
    }

    private float _lastLimitTime = -10f;

    private void PlayLimit(Axis a)
    {
        if (Time.time - _lastLimitTime < 1f)
            return;
        _lastLimitTime = Time.time;
        PlayAt(limitHit, a.at.position, limitVolume, Random.Range(0.95f, 1.05f));
    }

    private static void DriveLoop(AudioSource s, float volume, float pitch)
    {
        if (s == null || s.clip == null)
            return;
        s.volume = volume;
        s.pitch = pitch;
        if (volume > 0.001f)
        {
            if (!s.isPlaying)
            {
                s.time = Random.Range(0f, s.clip.length);
                s.Play();
            }
        }
        else if (s.isPlaying)
        {
            s.Stop();
        }
    }

    private void PlayAt(AudioClip clip, Vector3 pos, float volume, float pitch)
    {
        if (clip == null || _oneShots == null)
            return;
        AudioSource s = _oneShots[_next];
        _next = (_next + 1) % _oneShots.Length;
        s.transform.position = pos;
        s.clip = clip;
        s.pitch = pitch;
        s.volume = Mathf.Clamp01(volume * masterVolume);
        s.Play();
    }
}

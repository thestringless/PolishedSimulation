using UnityEngine;
using UnityEngine.Audio;

namespace Sefpro.Assembly
{
    /// <summary>
    /// 3D sound for the chain sling. Listens to MoldCraneLiftingPoint events and plays:
    /// - a rattle loop that follows the chains and swells with how fast the links move (swinging, dragging, carrying);
    /// - a link clink at each chain contact with the floor, the mould or a stand (rate-limited, louder for harder hits);
    /// - a drop cascade when the chain lands hard;
    /// - socket in / out clunks, hook / unhook clanks, Ring impacts, a strain clank when the mould leaves the ground,
    ///   and a short rattle when a hand grabs the Ring or a chain end.
    /// Put it next to MoldCraneLiftingPoint (on the Ring). Clips live in Assets/Audio/Chain.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChainSlingAudio : MonoBehaviour
    {
        [Tooltip("The sling to listen to. Found on this GameObject when empty.")]
        public MoldCraneLiftingPoint sling;

        [Tooltip("Optional mixer group for every chain sound.")]
        public AudioMixerGroup output;

        [Header("Clips")]
        public AudioClip[] linkClinks;
        public AudioClip rattleLoop;
        public AudioClip chainDrop;
        [Tooltip("Played when a hand picks up the Ring or a chain end. Falls back to Socket Out if empty.")]
        public AudioClip pickUp;
        public AudioClip socketIn;
        public AudioClip socketOut;
        public AudioClip ringHook;
        public AudioClip ringUnhook;
        public AudioClip[] ringImpacts;
        public AudioClip chainTaut;

        [Header("Levels")]
        [Range(0f, 1f)] public float masterVolume = 1f;
        [Range(0f, 1f)] public float rattleVolume = 0.55f;
        [Range(0f, 1f)] public float clinkVolume = 0.6f;
        [Range(0f, 1f)] public float eventVolume = 0.9f;

        [Header("Behaviour")]
        [Tooltip("Link speed (m/s) at which the rattle loop starts to be heard.")]
        public float rattleStartSpeed = 0.06f;
        [Tooltip("Link speed (m/s) at which the rattle loop is at full volume.")]
        public float rattleFullSpeed = 0.9f;
        [Tooltip("Most link clinks per second (the chain can report hundreds of contacts).")]
        public float maxClinksPerSecond = 14f;
        [Tooltip("Link impact speed (m/s) that plays the full drop cascade instead of a single clink.")]
        public float dropImpactSpeed = 2.2f;
        [Tooltip("Sounds are muted for this long after the scene starts (the sockets grab their chains on the first frames).")]
        public float startupMute = 2f;

        [Header("3D")]
        public float minDistance = 0.5f;
        public float maxDistance = 14f;
        [Range(4, 24)] public int voices = 10;

        private AudioSource[] _pool;
        private int _next;
        private AudioSource _rattle;
        private float _rattleLevel;
        private float _clinkBudget;
        private float _lastDropTime = -10f;
        private float _lastGrabTime = -10f;
        private float _lastRingImpactTime = -10f;
        private float _enabledAt;

        private void Awake()
        {
            if (sling == null)
                sling = GetComponent<MoldCraneLiftingPoint>();

            var root = new GameObject("ChainSlingAudio_Voices").transform;
            root.SetParent(transform, false);
            _pool = new AudioSource[Mathf.Max(4, voices)];
            for (int i = 0; i < _pool.Length; i++)
            {
                var go = new GameObject($"Voice_{i:00}");
                go.transform.SetParent(root, false);
                _pool[i] = MakeSource(go, false);
            }

            var rattleGo = new GameObject("ChainSlingAudio_Rattle");
            rattleGo.transform.SetParent(transform, false);
            _rattle = MakeSource(rattleGo, true);
            _rattle.clip = rattleLoop;
            _rattle.volume = 0f;
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
            s.outputAudioMixerGroup = output;
            return s;
        }

        private void OnEnable()
        {
            _enabledAt = Time.time;
            if (sling == null)
                return;
            sling.LinkImpact += OnLinkImpact;
            sling.RingImpact += OnRingImpact;
            sling.ChainEndSocketChanged += OnSocketChanged;
            sling.RingHookChanged += OnHookChanged;
            sling.MouldLiftChanged += OnLiftChanged;
            sling.SlingGrabbed += OnGrabbed;
        }

        private void OnDisable()
        {
            if (_rattle != null)
                _rattle.Stop();
            if (sling == null)
                return;
            sling.LinkImpact -= OnLinkImpact;
            sling.RingImpact -= OnRingImpact;
            sling.ChainEndSocketChanged -= OnSocketChanged;
            sling.RingHookChanged -= OnHookChanged;
            sling.MouldLiftChanged -= OnLiftChanged;
            sling.SlingGrabbed -= OnGrabbed;
        }

        private bool Muted => Time.time - _enabledAt < startupMute;

        private void Update()
        {
            _clinkBudget = Mathf.Min(maxClinksPerSecond * 0.25f, _clinkBudget + maxClinksPerSecond * Time.deltaTime);

            if (sling == null || _rattle == null || rattleLoop == null)
                return;

            // Rattle loop follows the chains and swells with link speed.
            float speed = sling.ChainMotionSpeed;
            float target = Muted ? 0f : Mathf.Clamp01(Mathf.InverseLerp(rattleStartSpeed, rattleFullSpeed, speed));
            float rate = target > _rattleLevel ? 10f : 3f; // quick attack, slower release
            _rattleLevel = Mathf.MoveTowards(_rattleLevel, target, rate * Time.deltaTime);

            _rattle.transform.position = sling.ChainCentroid;
            _rattle.volume = _rattleLevel * _rattleLevel * rattleVolume * masterVolume;
            _rattle.pitch = 0.9f + 0.25f * _rattleLevel;
            if (_rattleLevel > 0.001f)
            {
                if (!_rattle.isPlaying)
                {
                    _rattle.time = Random.Range(0f, rattleLoop.length);
                    _rattle.Play();
                }
            }
            else if (_rattle.isPlaying)
            {
                _rattle.Stop();
            }
        }

        private void OnLinkImpact(Vector3 pos, float speed)
        {
            if (Muted)
                return;
            if (speed >= dropImpactSpeed && chainDrop != null && Time.time - _lastDropTime > 0.6f)
            {
                _lastDropTime = Time.time;
                Play(chainDrop, pos, Mathf.Clamp01(speed / (dropImpactSpeed * 2f)) * eventVolume, Random.Range(0.92f, 1.08f));
                return;
            }
            if (_clinkBudget < 1f || linkClinks == null || linkClinks.Length == 0)
                return;
            _clinkBudget -= 1f;
            float vol = Mathf.Clamp01(speed / 2.5f) * clinkVolume;
            Play(linkClinks[Random.Range(0, linkClinks.Length)], pos, vol, Random.Range(0.85f, 1.2f));
        }

        private void OnRingImpact(Vector3 pos, float speed)
        {
            if (Muted || ringImpacts == null || ringImpacts.Length == 0 || Time.time - _lastRingImpactTime < 0.18f)
                return;
            _lastRingImpactTime = Time.time;
            Play(ringImpacts[Random.Range(0, ringImpacts.Length)], pos, Mathf.Clamp01(speed / 3f) * eventVolume, Random.Range(0.9f, 1.1f));
        }

        private void OnSocketChanged(int chain, bool socketed, Vector3 pos)
        {
            if (Muted)
                return;
            Play(socketed ? socketIn : socketOut, pos, eventVolume, Random.Range(0.95f, 1.05f));
        }

        private void OnHookChanged(bool hooked, Vector3 pos)
        {
            if (Muted)
                return;
            Play(hooked ? ringHook : ringUnhook, pos, eventVolume, Random.Range(0.96f, 1.04f));
        }

        private void OnLiftChanged(bool lifted, Vector3 pos)
        {
            if (Muted || !lifted)
                return;
            Play(chainTaut, pos, eventVolume, 1f);
        }

        private void OnGrabbed(Vector3 pos)
        {
            if (Muted || Time.time - _lastGrabTime < 0.25f)
                return;
            _lastGrabTime = Time.time;
            if (pickUp != null)
                Play(pickUp, pos, eventVolume, Random.Range(0.95f, 1.05f));
            else
                Play(socketOut, pos, eventVolume * 0.45f, Random.Range(1.05f, 1.2f));
        }

        private void Play(AudioClip clip, Vector3 pos, float volume, float pitch)
        {
            if (clip == null || volume <= 0.001f || _pool == null)
                return;
            AudioSource s = _pool[_next];
            _next = (_next + 1) % _pool.Length;
            s.transform.position = pos;
            s.clip = clip;
            s.pitch = pitch;
            s.volume = Mathf.Clamp01(volume * masterVolume);
            s.Play();
        }
    }
}

using System.Collections;
using System.Collections.Generic;
using Triggle.Core;
using UnityEngine;

namespace Triggle.Audio
{
    /// <summary>
    /// The game's audio hub: maps gameplay events onto sound effects, plays looping music, and applies
    /// the master / music / SFX levels from <see cref="TrigglePrefs"/>.
    /// </summary>
    /// <remarks>
    /// Clips ship with the project under <c>Assets/Audio/Triggle</c> (CC0 sound effects, one CC-BY music
    /// track - see CREDITS.txt). Any clip left empty falls back to a synthesised tone so the game is
    /// never silent.
    /// <para>
    /// SFX play through a small round-robin pool of AudioSources rather than a single one, so a cascade
    /// of claims layers properly instead of each sound cutting off the last. Music uses two sources so
    /// tracks can cross-fade.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SoundManager : MonoBehaviour
    {
        [Header("Sound Effects")]
        [Tooltip("Played when a peg is added to the selection.")]
        [SerializeField] private AudioClip pegSelectClip;

        [Tooltip("Played when a rubber band is committed to the board.")]
        [SerializeField] private AudioClip bandPlaceClip;

        [Tooltip("Played once per claimed triangle. Pitch rises through a cascade.")]
        [SerializeField] private AudioClip cellClaimClip;

        [Tooltip("Played when a claim token lands on the board.")]
        [SerializeField] private AudioClip tokenLandClip;

        [Tooltip("Played when a selection is rejected.")]
        [SerializeField] private AudioClip invalidMoveClip;

        [Tooltip("Played when a UI button is pressed.")]
        [SerializeField] private AudioClip uiClickClip;

        [Tooltip("Played when a panel is closed or a step is undone.")]
        [SerializeField] private AudioClip uiBackClip;

        [Tooltip("Sweetener layered under the win fanfare.")]
        [SerializeField] private AudioClip winAccentClip;

        [Tooltip("Played once at game over. Synthesised when empty.")]
        [SerializeField] private AudioClip winFanfareClip;

        [Header("Music")]
        [Tooltip("Looping track for the menu and for play.")]
        [SerializeField] private AudioClip musicTrack;

        [Tooltip("Optional quieter bed layered under everything. Leave empty to skip.")]
        [SerializeField] private AudioClip ambienceTrack;

        [SerializeField, Min(0f)] private float musicFadeDuration = 1.2f;

        [Tooltip("Scales the ambience bed relative to the music level.")]
        [SerializeField, Range(0f, 1f)] private float ambienceMix = 0.35f;

        [Header("SFX Pool")]
        [Tooltip("Concurrent sound effects before the oldest is reused.")]
        [SerializeField, Range(2, 12)] private int sfxVoices = 6;

        [Header("Chain Pitch Ladder")]
        [Tooltip("Semitone step added per consecutive claim within a single move.")]
        [SerializeField, Range(0f, 4f)] private float claimPitchStepSemitones = 1.5f;

        [SerializeField, Range(0, 12)] private int maxPitchSteps = 6;

        [Header("Synthesis Fallback")]
        [Tooltip("Generate placeholder tones for any clip left empty.")]
        [SerializeField] private bool generateMissingClips = true;

        private readonly List<AudioSource> _sfxSources = new List<AudioSource>(8);
        private readonly List<AudioClip> _generatedClips = new List<AudioClip>(8);
        private AudioSource _musicSource;
        private AudioSource _ambienceSource;
        private Coroutine _musicFade;

        private int _sfxCursor;
        private int _claimStreak;
        private int _lastSelectionCount;

        /// <summary>Most recently created instance, for UI code that needs a direct call.</summary>
        public static SoundManager Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            TrigglePrefs.Load();

            _musicSource = CreateSource("Music", true);
            _ambienceSource = CreateSource("Ambience", true);

            for (int i = 0; i < sfxVoices; i++) _sfxSources.Add(CreateSource($"SFX_{i}", false));

            if (generateMissingClips) GenerateFallbackClips();
            ApplyVolumes();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            for (int i = 0; i < _generatedClips.Count; i++)
            {
                if (_generatedClips[i] == null) continue;
                if (Application.isPlaying) Destroy(_generatedClips[i]);
                else DestroyImmediate(_generatedClips[i]);
            }

            _generatedClips.Clear();
        }

        private void Start()
        {
            PlayMusic(musicTrack);

            if (ambienceTrack != null && _ambienceSource != null)
            {
                _ambienceSource.clip = ambienceTrack;
                _ambienceSource.volume = TrigglePrefs.EffectiveMusicVolume * ambienceMix;
                _ambienceSource.Play();
            }
        }

        private AudioSource CreateSource(string sourceName, bool loop)
        {
            var go = new GameObject($"AudioSource_{sourceName}");
            go.transform.SetParent(transform, false);

            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 0f;   // 2D: the board is always on screen.
            source.rolloffMode = AudioRolloffMode.Linear;
            return source;
        }

        private void GenerateFallbackClips()
        {
            // Short, percussive placeholders for anything the project did not ship a clip for.
            pegSelectClip ??= Synthesise("Tone_PegSelect", 880f, 0.07f, 10f, Waveform.Triangle);
            bandPlaceClip ??= Synthesise("Tone_BandPlace", 330f, 0.16f, 7f, Waveform.Square);
            cellClaimClip ??= Synthesise("Tone_CellClaim", 660f, 0.22f, 5f, Waveform.Sine);
            tokenLandClip ??= Synthesise("Tone_TokenLand", 420f, 0.10f, 9f, Waveform.Sine);
            invalidMoveClip ??= Synthesise("Tone_Invalid", 150f, 0.18f, 9f, Waveform.Square);
            uiClickClip ??= Synthesise("Tone_UiClick", 1240f, 0.06f, 12f, Waveform.Triangle);
            uiBackClip ??= Synthesise("Tone_UiBack", 620f, 0.07f, 12f, Waveform.Triangle);
            winFanfareClip ??= SynthesiseFanfare("Tone_Fanfare");
        }

        private void OnEnable()
        {
            GameEvents.OnSelectionChanged += HandleSelectionChanged;
            GameEvents.OnBandPlaced += HandleBandPlaced;
            GameEvents.OnCellClaimed += HandleCellClaimed;
            GameEvents.OnInvalidMove += HandleInvalidMove;
            GameEvents.OnUiClick += HandleUiClick;
            GameEvents.OnGameOver += HandleGameOver;
            GameEvents.OnGameReset += HandleGameReset;

            TrigglePrefs.OnVolumesChanged += HandleVolumesChanged;
        }

        private void OnDisable()
        {
            GameEvents.OnSelectionChanged -= HandleSelectionChanged;
            GameEvents.OnBandPlaced -= HandleBandPlaced;
            GameEvents.OnCellClaimed -= HandleCellClaimed;
            GameEvents.OnInvalidMove -= HandleInvalidMove;
            GameEvents.OnUiClick -= HandleUiClick;
            GameEvents.OnGameOver -= HandleGameOver;
            GameEvents.OnGameReset -= HandleGameReset;

            TrigglePrefs.OnVolumesChanged -= HandleVolumesChanged;
        }

        // ------------------------------------------------------------------ volume

        private void HandleVolumesChanged(float master, float music, float sfx) => ApplyVolumes();

        /// <summary>Pushes the stored levels onto the live sources.</summary>
        public void ApplyVolumes()
        {
            if (_musicSource != null && _musicFade == null)
                _musicSource.volume = TrigglePrefs.EffectiveMusicVolume;

            if (_ambienceSource != null)
                _ambienceSource.volume = TrigglePrefs.EffectiveMusicVolume * ambienceMix;
        }

        // ------------------------------------------------------------------ music

        /// <summary>Cross-fades to a track. Passing null fades the music out.</summary>
        public void PlayMusic(AudioClip clip)
        {
            if (_musicSource == null) return;

            if (_musicFade != null) StopCoroutine(_musicFade);
            _musicFade = StartCoroutine(MusicFadeRoutine(clip));
        }

        private IEnumerator MusicFadeRoutine(AudioClip clip)
        {
            float target = TrigglePrefs.EffectiveMusicVolume;

            // Fade the outgoing track down first, if one is playing.
            if (_musicSource.isPlaying && musicFadeDuration > 0f)
            {
                float from = _musicSource.volume;
                float elapsed = 0f;

                while (elapsed < musicFadeDuration * 0.5f)
                {
                    elapsed += Time.unscaledDeltaTime;
                    _musicSource.volume = Mathf.Lerp(from, 0f, elapsed / (musicFadeDuration * 0.5f));
                    yield return null;
                }
            }

            _musicSource.Stop();

            if (clip == null)
            {
                _musicSource.volume = 0f;
                _musicFade = null;
                yield break;
            }

            _musicSource.clip = clip;
            _musicSource.volume = 0f;
            _musicSource.Play();

            if (musicFadeDuration > 0f)
            {
                float elapsed = 0f;
                while (elapsed < musicFadeDuration)
                {
                    elapsed += Time.unscaledDeltaTime;

                    // Re-read the target each frame so dragging the slider mid-fade still tracks.
                    target = TrigglePrefs.EffectiveMusicVolume;
                    _musicSource.volume = Mathf.Lerp(0f, target, elapsed / musicFadeDuration);
                    yield return null;
                }
            }

            _musicSource.volume = TrigglePrefs.EffectiveMusicVolume;
            _musicFade = null;
        }

        // ------------------------------------------------------------------ sfx

        /// <summary>Plays a one-shot through the next voice in the pool.</summary>
        public void PlaySfx(AudioClip clip, float pitch = 1f, float volumeScale = 1f)
        {
            if (clip == null || _sfxSources.Count == 0) return;

            AudioSource source = _sfxSources[_sfxCursor];
            _sfxCursor = (_sfxCursor + 1) % _sfxSources.Count;

            source.pitch = Mathf.Clamp(pitch, 0.25f, 3f);
            source.PlayOneShot(clip, Mathf.Clamp01(TrigglePrefs.EffectiveSfxVolume * volumeScale));
        }

        /// <summary>Plays the standard UI click. Useful for controls wired directly in the inspector.</summary>
        public void PlayUiClick() => PlaySfx(uiClickClip);

        /// <summary>Plays the "back"/dismiss sound.</summary>
        public void PlayUiBack() => PlaySfx(uiBackClip);

        // ------------------------------------------------------------------ handlers

        private void HandleSelectionChanged(IReadOnlyList<Peg> selection)
        {
            int count = selection?.Count ?? 0;

            // Only adding a peg makes a sound; removals and clears stay silent.
            if (count > _lastSelectionCount)
            {
                float pitch = Mathf.Pow(2f, (count - 1) * 1.5f / 12f);
                PlaySfx(pegSelectClip, pitch);
            }

            _lastSelectionCount = count;
        }

        private void HandleBandPlaced(PlayerId player, BandPlacement band)
        {
            _claimStreak = 0;
            _lastSelectionCount = 0;
            PlaySfx(bandPlaceClip);
        }

        private void HandleCellClaimed(TriangleCell cell)
        {
            int step = Mathf.Min(_claimStreak, maxPitchSteps);
            float pitch = Mathf.Pow(2f, step * claimPitchStepSemitones / 12f);
            _claimStreak++;

            PlaySfx(cellClaimClip, pitch);
            PlaySfx(tokenLandClip, pitch, 0.5f);
        }

        private void HandleInvalidMove(string reason) => PlaySfx(invalidMoveClip);

        private void HandleUiClick() => PlaySfx(uiClickClip);

        private void HandleGameOver(GameResult result)
        {
            PlaySfx(winFanfareClip, 1f, 1f);
            if (winAccentClip != null) PlaySfx(winAccentClip, 1f, 0.8f);
        }

        private void HandleGameReset()
        {
            _claimStreak = 0;
            _lastSelectionCount = 0;
        }

        // ------------------------------------------------------------------ synthesis

        private enum Waveform { Sine, Triangle, Square }

        /// <summary>
        /// Builds a mono clip: one oscillator with an exponential decay envelope and a short attack ramp
        /// to avoid a click on note-on.
        /// </summary>
        private AudioClip Synthesise(string clipName, float frequency, float duration, float decay, Waveform waveform)
        {
            const int sampleRate = 44100;
            int sampleCount = Mathf.Max(16, (int)(sampleRate * duration));

            var data = new float[sampleCount];
            int attackSamples = Mathf.Max(1, (int)(sampleRate * 0.004f));

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleRate;
                float phase = frequency * t;

                float sample = waveform switch
                {
                    Waveform.Square => Mathf.Sign(Mathf.Sin(phase * Mathf.PI * 2f)) * 0.5f,
                    Waveform.Triangle => Mathf.PingPong(phase * 2f, 1f) * 2f - 1f,
                    _ => Mathf.Sin(phase * Mathf.PI * 2f)
                };

                float envelope = Mathf.Exp(-decay * t * 4f);
                if (i < attackSamples) envelope *= i / (float)attackSamples;

                data[i] = sample * envelope * 0.6f;
            }

            return CreateClip(clipName, data, sampleRate);
        }

        /// <summary>Builds a short rising arpeggio for the victory sting.</summary>
        private AudioClip SynthesiseFanfare(string clipName)
        {
            const int sampleRate = 44100;
            float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f };   // C5 E5 G5 C6
            const float noteDuration = 0.16f;
            const float tail = 0.5f;

            int sampleCount = (int)(sampleRate * (notes.Length * noteDuration + tail));
            var data = new float[sampleCount];

            for (int n = 0; n < notes.Length; n++)
            {
                int startSample = (int)(sampleRate * n * noteDuration);
                int noteSamples = (int)(sampleRate * (noteDuration + tail));

                for (int i = 0; i < noteSamples; i++)
                {
                    int target = startSample + i;
                    if (target >= sampleCount) break;

                    float t = i / (float)sampleRate;
                    float envelope = Mathf.Exp(-3.2f * t);
                    float sample = Mathf.Sin(notes[n] * t * Mathf.PI * 2f) * 0.32f;

                    // Fifth above, quieter, to thicken the chord.
                    sample += Mathf.Sin(notes[n] * 1.5f * t * Mathf.PI * 2f) * 0.12f;

                    data[target] += sample * envelope;
                }
            }

            // Normalise so summed notes never clip.
            float peak = 0.0001f;
            for (int i = 0; i < sampleCount; i++) peak = Mathf.Max(peak, Mathf.Abs(data[i]));

            if (peak > 0.95f)
            {
                float gain = 0.95f / peak;
                for (int i = 0; i < sampleCount; i++) data[i] *= gain;
            }

            return CreateClip(clipName, data, sampleRate);
        }

        private AudioClip CreateClip(string clipName, float[] data, int sampleRate)
        {
            AudioClip clip = AudioClip.Create(clipName, data.Length, 1, sampleRate, false);
            clip.SetData(data, 0);
            clip.hideFlags = HideFlags.DontSave;

            _generatedClips.Add(clip);
            return clip;
        }
    }
}

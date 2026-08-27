using System;
using UnityEngine;

namespace Triggle.Core
{
    /// <summary>
    /// Central store for player-facing options, persisted with <see cref="PlayerPrefs"/>.
    /// </summary>
    /// <remarks>
    /// Every setter raises a change event so the audio system and board visuals can react without the
    /// Settings screen holding references to them.
    /// <para>
    /// Board theme and board size are marked "pre-game only" by convention: the Settings screen
    /// disables those controls once a match is running, because changing the radius requires
    /// regenerating the lattice, which would invalidate every placed band mid-game.
    /// </para>
    /// </remarks>
    public static class TrigglePrefs
    {
        private const string KeyMasterVolume = "triggle.audio.master";
        private const string KeyMusicVolume = "triggle.audio.music";
        private const string KeySfxVolume = "triggle.audio.sfx";
        private const string KeyBoardTheme = "triggle.board.theme";
        private const string KeyBoardRadius = "triggle.board.radius";

        /// <summary>Smallest and largest board radius offered in Settings.</summary>
        public const int MinBoardRadius = 3;
        public const int MaxBoardRadius = 5;

        private static bool _loaded;
        private static float _master = 0.85f;
        private static float _music = 0.55f;
        private static float _sfx = 0.80f;
        private static int _boardTheme;
        private static int _boardRadius = 3;

        /// <summary>Raised when any volume changes. Payload: master, music, sfx (all 0-1).</summary>
        public static event Action<float, float, float> OnVolumesChanged;

        /// <summary>Raised when the selected board theme index changes.</summary>
        public static event Action<int> OnBoardThemeChanged;

        /// <summary>Raised when the selected board radius changes.</summary>
        public static event Action<int> OnBoardRadiusChanged;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            _master = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyMasterVolume, 0.85f));
            _music = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyMusicVolume, 0.55f));
            _sfx = Mathf.Clamp01(PlayerPrefs.GetFloat(KeySfxVolume, 0.80f));
            _boardTheme = Mathf.Max(0, PlayerPrefs.GetInt(KeyBoardTheme, 0));
            _boardRadius = Mathf.Clamp(PlayerPrefs.GetInt(KeyBoardRadius, 3), MinBoardRadius, MaxBoardRadius);
        }

        public static float MasterVolume
        {
            get { Load(); return _master; }
            set => SetVolumes(value, MusicVolume, SfxVolume);
        }

        public static float MusicVolume
        {
            get { Load(); return _music; }
            set => SetVolumes(MasterVolume, value, SfxVolume);
        }

        public static float SfxVolume
        {
            get { Load(); return _sfx; }
            set => SetVolumes(MasterVolume, MusicVolume, value);
        }

        /// <summary>Index into the board theme library.</summary>
        public static int BoardThemeIndex
        {
            get { Load(); return _boardTheme; }
            set
            {
                Load();
                int clamped = Mathf.Max(0, value);
                if (clamped == _boardTheme) return;

                _boardTheme = clamped;
                PlayerPrefs.SetInt(KeyBoardTheme, clamped);
                PlayerPrefs.Save();

                OnBoardThemeChanged?.Invoke(clamped);
            }
        }

        /// <summary>
        /// Hexagon radius for the next match. Clamped to a range where every edge is reachable by a
        /// 4-peg band (radius must be at least 3).
        /// </summary>
        public static int BoardRadius
        {
            get { Load(); return _boardRadius; }
            set
            {
                Load();
                int clamped = Mathf.Clamp(value, MinBoardRadius, MaxBoardRadius);
                if (clamped == _boardRadius) return;

                _boardRadius = clamped;
                PlayerPrefs.SetInt(KeyBoardRadius, clamped);
                PlayerPrefs.Save();

                OnBoardRadiusChanged?.Invoke(clamped);
            }
        }

        /// <summary>Writes all three volumes at once and raises a single change event.</summary>
        public static void SetVolumes(float master, float music, float sfx)
        {
            Load();

            _master = Mathf.Clamp01(master);
            _music = Mathf.Clamp01(music);
            _sfx = Mathf.Clamp01(sfx);

            PlayerPrefs.SetFloat(KeyMasterVolume, _master);
            PlayerPrefs.SetFloat(KeyMusicVolume, _music);
            PlayerPrefs.SetFloat(KeySfxVolume, _sfx);
            PlayerPrefs.Save();

            OnVolumesChanged?.Invoke(_master, _music, _sfx);
        }

        /// <summary>Effective music level after the master fader.</summary>
        public static float EffectiveMusicVolume => MasterVolume * MusicVolume;

        /// <summary>Effective SFX level after the master fader.</summary>
        public static float EffectiveSfxVolume => MasterVolume * SfxVolume;

        /// <summary>Restores defaults and notifies listeners.</summary>
        public static void ResetToDefaults()
        {
            _loaded = true;
            SetVolumes(0.85f, 0.55f, 0.80f);
            BoardThemeIndex = 0;
            BoardRadius = 3;
        }
    }
}

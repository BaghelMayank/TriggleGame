using System.Collections.Generic;
using Triggle.Core;
using Triggle.Grid;
using UnityEngine;

namespace Triggle.Visuals
{
    /// <summary>
    /// Holds the available <see cref="BoardTheme"/> assets and applies the selected one to the camera,
    /// the board surface and the pegs.
    /// </summary>
    /// <remarks>
    /// Listens to <see cref="TrigglePrefs.OnBoardThemeChanged"/>, so the Settings screen only has to
    /// write the preference - it never touches renderers itself.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class BoardThemeLibrary : MonoBehaviour
    {
        [Header("Themes")]
        [Tooltip("Selectable themes, in the order the Settings picker shows them.")]
        [SerializeField] private BoardTheme[] themes = new BoardTheme[0];

        [Header("Targets")]
        [SerializeField] private BoardVisuals boardVisuals;
        [SerializeField] private BoardManager board;
        [SerializeField] private Camera targetCamera;

        [Tooltip("Shared peg material. Retinted per theme, so all pegs update at once.")]
        [SerializeField] private Material pegMaterial;

        public int ThemeCount => themes?.Length ?? 0;

        /// <summary>The theme currently applied.</summary>
        public BoardTheme Current => GetTheme(TrigglePrefs.BoardThemeIndex);

        public IReadOnlyList<BoardTheme> Themes => themes;

        private void Awake()
        {
            if (boardVisuals == null) boardVisuals = FindObjectOfType<BoardVisuals>();
            if (board == null) board = FindObjectOfType<BoardManager>();
            if (targetCamera == null) targetCamera = Camera.main;
        }

        private void OnEnable()
        {
            TrigglePrefs.OnBoardThemeChanged += HandleThemeChanged;
            GameEvents.OnBoardGenerated += ApplyCurrent;
        }

        private void OnDisable()
        {
            TrigglePrefs.OnBoardThemeChanged -= HandleThemeChanged;
            GameEvents.OnBoardGenerated -= ApplyCurrent;
        }

        private void Start()
        {
            ApplyCurrent();
        }

        public BoardTheme GetTheme(int index)
        {
            if (themes == null || themes.Length == 0) return null;
            return themes[Mathf.Clamp(index, 0, themes.Length - 1)];
        }

        /// <summary>Display name for the picker, safe for missing assets.</summary>
        public string GetThemeName(int index)
        {
            BoardTheme theme = GetTheme(index);
            return theme != null ? theme.displayName : $"Theme {index + 1}";
        }

        private void HandleThemeChanged(int index) => ApplyCurrent();

        /// <summary>Applies the selected theme to every target.</summary>
        public void ApplyCurrent()
        {
            BoardTheme theme = Current;
            if (theme == null) return;

            if (targetCamera == null) targetCamera = Camera.main;
            if (targetCamera != null)
            {
                targetCamera.clearFlags = CameraClearFlags.SolidColor;
                targetCamera.backgroundColor = theme.backgroundColor;
            }

            if (pegMaterial != null)
            {
                MaterialUtility.SetColor(pegMaterial, theme.pegColor);
                MaterialUtility.SetSmoothness(pegMaterial, 0.45f);
            }

            if (boardVisuals != null) boardVisuals.ApplyTheme(theme);
        }
    }
}

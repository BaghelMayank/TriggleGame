using System.Collections.Generic;
using Triggle.Core;
using Triggle.Gameplay;
using Triggle.Grid;
using UnityEngine;

namespace Triggle.Visuals
{
    /// <summary>
    /// Drives every peg's <see cref="PegVisualState"/> from the current selection, the hovered peg and
    /// the set of legal continuations reported by <see cref="MoveValidator"/>.
    /// </summary>
    /// <remarks>
    /// State priority, highest first: Selected, Hovered, Selectable, Disabled, Idle. Recomputation is
    /// event-driven (selection change, hover change, band placed, turn start) rather than per frame;
    /// the smooth colour/scale interpolation lives in <see cref="PegComponent"/>.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class PegHighlighter : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;

        [Header("Behaviour")]
        [Tooltip("Grey out pegs that cannot take part in any band that is still playable.")]
        [SerializeField] private bool dimUnplayablePegs = true;

        [Tooltip("Highlight legal continuations even before the first peg is picked.")]
        [SerializeField] private bool highlightLegalStartPegs;

        private readonly Dictionary<Peg, PegComponent> _views = new Dictionary<Peg, PegComponent>();
        private readonly List<Peg> _selection = new List<Peg>(4);
        private readonly HashSet<Peg> _selectionSet = new HashSet<Peg>();
        private readonly HashSet<Peg> _selectablePegs = new HashSet<Peg>();
        private Peg _hoveredPeg;
        private bool _inputAllowed;

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
        }

        private void OnEnable()
        {
            GameEvents.OnBoardGenerated += RebuildViewCache;
            GameEvents.OnSelectionChanged += HandleSelectionChanged;
            GameEvents.OnPegHovered += HandlePegHovered;
            GameEvents.OnTurnStarted += HandleTurnStarted;
            GameEvents.OnBandPlaced += HandleBandPlaced;
            GameEvents.OnPhaseChanged += HandlePhaseChanged;
            GameEvents.OnGameOver += HandleGameOver;
        }

        private void OnDisable()
        {
            GameEvents.OnBoardGenerated -= RebuildViewCache;
            GameEvents.OnSelectionChanged -= HandleSelectionChanged;
            GameEvents.OnPegHovered -= HandlePegHovered;
            GameEvents.OnTurnStarted -= HandleTurnStarted;
            GameEvents.OnBandPlaced -= HandleBandPlaced;
            GameEvents.OnPhaseChanged -= HandlePhaseChanged;
            GameEvents.OnGameOver -= HandleGameOver;
        }

        private void Start()
        {
            if (_views.Count == 0) RebuildViewCache();
        }

        /// <summary>Maps each peg model to its scene component. Called whenever the board is rebuilt.</summary>
        private void RebuildViewCache()
        {
            _views.Clear();

            if (flowController == null || flowController.Board == null) return;

            IReadOnlyList<Peg> pegs = flowController.Board.Pegs;
            for (int i = 0; i < pegs.Count; i++)
            {
                Peg peg = pegs[i];
                if (peg.View == null) continue;

                var component = peg.View.GetComponent<PegComponent>();
                if (component != null) _views[peg] = component;
            }

            Refresh();
        }

        private void HandleSelectionChanged(IReadOnlyList<Peg> selection)
        {
            // The payload is the input controller's live buffer, so copy before caching it.
            _selection.Clear();
            _selectionSet.Clear();

            if (selection != null)
            {
                for (int i = 0; i < selection.Count; i++)
                {
                    Peg peg = selection[i];
                    if (peg == null) continue;

                    _selection.Add(peg);
                    _selectionSet.Add(peg);
                }
            }

            Refresh();
        }

        private void HandlePegHovered(Peg peg)
        {
            _hoveredPeg = peg;
            Refresh();
        }

        private void HandleTurnStarted(PlayerId player)
        {
            _inputAllowed = true;
            Refresh();
        }

        private void HandleBandPlaced(PlayerId player, BandPlacement band) => Refresh();

        private void HandlePhaseChanged(GamePhase phase)
        {
            bool allowed = phase == GamePhase.WaitingForInput;
            if (allowed == _inputAllowed) return;

            _inputAllowed = allowed;
            Refresh();
        }

        private void HandleGameOver(GameResult result)
        {
            _inputAllowed = false;
            _selection.Clear();
            _selectionSet.Clear();
            _hoveredPeg = null;
            Refresh();
        }

        /// <summary>Recomputes the legal continuation set and pushes a state onto every peg.</summary>
        private void Refresh()
        {
            if (_views.Count == 0) return;

            _selectablePegs.Clear();

            MoveValidator validator = flowController != null ? flowController.Validator : null;

            if (validator != null && _inputAllowed && (_selection.Count > 0 || highlightLegalStartPegs))
            {
                // GetValidNextPegs returns a validator-owned buffer, so copy it out immediately.
                IReadOnlyList<Peg> legal = validator.GetValidNextPegs(_selection);
                for (int i = 0; i < legal.Count; i++) _selectablePegs.Add(legal[i]);
            }

            foreach (KeyValuePair<Peg, PegComponent> entry in _views)
                entry.Value.SetState(ResolveState(entry.Key, validator));
        }

        private PegVisualState ResolveState(Peg peg, MoveValidator validator)
        {
            if (_selectionSet.Contains(peg)) return PegVisualState.Selected;
            if (_hoveredPeg == peg && _inputAllowed) return PegVisualState.Hovered;
            if (_selectablePegs.Contains(peg)) return PegVisualState.Selectable;

            if (dimUnplayablePegs && validator != null && !HasPlayableBand(peg, validator))
                return PegVisualState.Disabled;

            return PegVisualState.Idle;
        }

        /// <summary>True when at least one band through this peg can still be legally placed.</summary>
        private static bool HasPlayableBand(Peg peg, MoveValidator validator)
        {
            List<BandPlacement> bands = peg.Bands;
            for (int i = 0; i < bands.Count; i++)
                if (validator.IsBandLegal(bands[i], out _)) return true;

            return false;
        }
    }
}

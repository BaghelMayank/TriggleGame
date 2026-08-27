using System.Collections.Generic;
using Triggle.Core;
using Triggle.Gameplay;
using Triggle.Grid;
using UnityEngine;

namespace Triggle.Interaction
{
    /// <summary>
    /// Translates mouse and touch input into a peg selection buffer and submits completed selections
    /// to <see cref="GameFlowController"/>.
    /// </summary>
    /// <remarks>
    /// Uses the legacy Input Manager (the project's Active Input Handling setting), so it works on
    /// desktop and touch without extra packages. Selection rules:
    /// <list type="bullet">
    /// <item>Clicking a legal peg appends it; clicking an already-selected peg removes it.</item>
    /// <item>Clicking empty space, right-clicking or pressing Escape clears the buffer.</item>
    /// <item>Clicking an illegal peg clears the buffer (configurable) and reports why.</item>
    /// <item>Reaching the required peg count submits the move automatically.</item>
    /// </list>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class InputController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;

        [Tooltip("Camera used for picking. Falls back to Camera.main when empty.")]
        [SerializeField] private Camera raycastCamera;

        [Header("Picking")]
        [Tooltip("Layers containing peg colliders. Default (everything) works for the generated board.")]
        [SerializeField] private LayerMask pegLayerMask = ~0;

        [SerializeField, Min(1f)] private float maxRaycastDistance = 500f;

        [Header("Behaviour")]
        [Tooltip("Clear the whole selection when the player picks an illegal peg.")]
        [SerializeField] private bool clearSelectionOnIllegalPick = true;

        [Tooltip("Clicking an already-selected peg removes it instead of being treated as illegal.")]
        [SerializeField] private bool allowDeselectByClicking = true;

        [Tooltip("Ignore clicks that land on UI. Requires an EventSystem in the scene.")]
        [SerializeField] private bool blockWhenPointerOverUI = true;

        private readonly List<Peg> _selection = new List<Peg>(4);
        private readonly HashSet<Peg> _legalNextPegs = new HashSet<Peg>();
        private Peg _hoveredPeg;

        /// <summary>Pegs picked so far this turn, in click order.</summary>
        public IReadOnlyList<Peg> Selection => _selection;

        /// <summary>The peg currently under the pointer, or null.</summary>
        public Peg HoveredPeg => _hoveredPeg;

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
            if (raycastCamera == null) raycastCamera = Camera.main;
        }

        private void OnEnable()
        {
            GameEvents.OnTurnStarted += HandleTurnStarted;
            GameEvents.OnGameReset += HandleGameReset;
            GameEvents.OnGameOver += HandleGameOver;
        }

        private void OnDisable()
        {
            GameEvents.OnTurnStarted -= HandleTurnStarted;
            GameEvents.OnGameReset -= HandleGameReset;
            GameEvents.OnGameOver -= HandleGameOver;
        }

        private void Update()
        {
            if (flowController == null) return;

            if (raycastCamera == null)
            {
                raycastCamera = Camera.main;
                if (raycastCamera == null) return;
            }

            if (!flowController.AcceptsInput)
            {
                SetHovered(null);
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                ClearSelection();
                return;
            }

            if (Input.GetMouseButtonDown(1))
            {
                // Right click steps back one peg, or clears when nothing is selected.
                if (_selection.Count > 0) RemoveLast();
                else ClearSelection();
                return;
            }

            if (!TryGetPointerPosition(out Vector3 pointer, out bool pressedThisFrame))
            {
                SetHovered(null);
                return;
            }

            Peg peg = RaycastPeg(pointer);
            SetHovered(peg);

            if (!pressedThisFrame) return;
            if (blockWhenPointerOverUI && IsPointerOverUI()) return;

            if (peg == null) ClearSelection();       // click outside the board cancels
            else HandlePegClicked(peg);
        }

        /// <summary>
        /// Reads pointer position from touch when available, otherwise from the mouse.
        /// <paramref name="pressedThisFrame"/> is true on the frame the primary button/touch went down.
        /// </summary>
        private bool TryGetPointerPosition(out Vector3 position, out bool pressedThisFrame)
        {
            if (Input.touchSupported && Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                position = touch.position;
                pressedThisFrame = touch.phase == TouchPhase.Began;
                return true;
            }

            if (!Input.mousePresent)
            {
                position = Vector3.zero;
                pressedThisFrame = false;
                return false;
            }

            position = Input.mousePosition;
            pressedThisFrame = Input.GetMouseButtonDown(0);
            return true;
        }

        private static bool IsPointerOverUI()
        {
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            return eventSystem != null && eventSystem.IsPointerOverGameObject();
        }

        private Peg RaycastPeg(Vector3 screenPosition)
        {
            Ray ray = raycastCamera.ScreenPointToRay(screenPosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, maxRaycastDistance, pegLayerMask)) return null;

            // The collider may sit on a child of the peg root, so search upward.
            var component = hit.collider.GetComponentInParent<PegComponent>();
            return component != null ? component.Peg : null;
        }

        private void HandlePegClicked(Peg peg)
        {
            if (_selection.Contains(peg))
            {
                if (allowDeselectByClicking)
                {
                    _selection.Remove(peg);
                    RefreshLegalNextPegs();
                    GameEvents.RaiseSelectionChanged(_selection);
                }
                return;
            }

            if (!_legalNextPegs.Contains(peg))
            {
                GameEvents.RaiseInvalidMove(_selection.Count == 0
                    ? "That peg cannot start a legal band."
                    : "That peg does not complete a legal band with your current selection.");

                if (clearSelectionOnIllegalPick) ClearSelection();
                return;
            }

            _selection.Add(peg);
            GameEvents.RaiseSelectionChanged(_selection);

            if (_selection.Count < flowController.Validator.RequiredPegCount)
            {
                RefreshLegalNextPegs();
                return;
            }

            // Buffer complete: hand it to the flow controller and reset regardless of the outcome,
            // so a rejected move never leaves a stale selection on screen.
            bool accepted = flowController.SubmitBandSelection(_selection);
            ClearSelection();

            if (!accepted) RefreshLegalNextPegs();
        }

        private void RemoveLast()
        {
            if (_selection.Count == 0) return;

            _selection.RemoveAt(_selection.Count - 1);
            RefreshLegalNextPegs();
            GameEvents.RaiseSelectionChanged(_selection);
        }

        /// <summary>Empties the selection buffer and rebuilds the legal-start set.</summary>
        public void ClearSelection()
        {
            bool hadSelection = _selection.Count > 0;
            _selection.Clear();
            RefreshLegalNextPegs();

            if (hadSelection) GameEvents.RaiseSelectionChanged(_selection);
        }

        /// <summary>
        /// Caches the legal next pegs for the current buffer. The validator returns a shared buffer,
        /// so the contents are copied into a local set immediately.
        /// </summary>
        private void RefreshLegalNextPegs()
        {
            _legalNextPegs.Clear();
            if (flowController == null || flowController.Validator == null) return;

            IReadOnlyList<Peg> legal = flowController.Validator.GetValidNextPegs(_selection);
            for (int i = 0; i < legal.Count; i++) _legalNextPegs.Add(legal[i]);
        }

        private void SetHovered(Peg peg)
        {
            if (_hoveredPeg == peg) return;

            _hoveredPeg = peg;
            GameEvents.RaisePegHovered(peg);
        }

        private void HandleTurnStarted(PlayerId player)
        {
            _selection.Clear();
            RefreshLegalNextPegs();
            GameEvents.RaiseSelectionChanged(_selection);
        }

        private void HandleGameReset()
        {
            _selection.Clear();
            _legalNextPegs.Clear();
            SetHovered(null);
            GameEvents.RaiseSelectionChanged(_selection);
        }

        private void HandleGameOver(GameResult result)
        {
            _selection.Clear();
            _legalNextPegs.Clear();
            SetHovered(null);
            GameEvents.RaiseSelectionChanged(_selection);
        }
    }
}

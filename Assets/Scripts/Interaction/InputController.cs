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
    /// desktop and touch without extra packages.
    /// <para>
    /// Two schemes, because the two devices are good at different things.
    /// </para>
    /// <para>
    /// <b>Tap</b>, wherever there is a mouse:
    /// <list type="bullet">
    /// <item>Clicking a legal peg appends it; clicking an already-selected peg removes it.</item>
    /// <item>Clicking empty space, right-clicking or pressing Escape clears the buffer.</item>
    /// <item>Clicking an illegal peg clears the buffer (configurable) and reports why.</item>
    /// <item>Reaching the required peg count submits the move automatically.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Drag</b>, on a handset: press a peg and pull across the run. Tapping four small pegs in a row
    /// is awkward with a finger - every one is a chance to miss, and a miss clears the lot - whereas a
    /// drag is a single continuous gesture with the band following as it goes. Reversing onto the
    /// previous peg backs up a step. The move submits the instant the run is complete, under the finger.
    /// </para>
    /// <para>
    /// The two paths are kept separate rather than unified: tap is what desktop players already know,
    /// and it should not shift underneath them to accommodate touch.
    /// </para>
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

        [Header("Selection Scheme")]
        [Tooltip("Auto drags on a touch device and taps everywhere else. Force one to test the other.")]
        [SerializeField] private SelectionScheme selectionScheme = SelectionScheme.Auto;

        /// <summary>How a band is picked out.</summary>
        public enum SelectionScheme
        {
            /// <summary>Drag on a touch-only device, tap wherever there is a mouse.</summary>
            Auto,

            /// <summary>Click one peg at a time.</summary>
            Tap,

            /// <summary>Hold and drag across the run.</summary>
            Drag
        }

        /// <summary>
        /// True when a band is drawn by dragging rather than tapping.
        /// </summary>
        /// <remarks>
        /// A mouse decides it, not the presence of a touchscreen: plenty of laptops report
        /// <see cref="Input.touchSupported"/> while the player is using a trackpad, and tapping is the
        /// better scheme there. A handset has no mouse, so it drags.
        /// </remarks>
        private bool UseDrag => selectionScheme switch
        {
            SelectionScheme.Tap => false,
            SelectionScheme.Drag => true,
            _ => Input.touchSupported && !Input.mousePresent
        };

        private readonly List<Peg> _selection = new List<Peg>(4);
        private readonly HashSet<Peg> _legalNextPegs = new HashSet<Peg>();
        private Peg _hoveredPeg;
        private bool _dragging;

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

            if (!TryGetPointer(out Vector3 pointer, out PointerPhase phase))
            {
                SetHovered(null);
                return;
            }

            Peg peg = RaycastPeg(pointer);
            SetHovered(peg);

            if (UseDrag) UpdateDrag(peg, phase);
            else UpdateTap(peg, phase);
        }

        /// <summary>
        /// Tap to pick, one peg at a time. The desktop scheme, unchanged.
        /// </summary>
        private void UpdateTap(Peg peg, PointerPhase phase)
        {
            if (phase != PointerPhase.Began) return;
            if (blockWhenPointerOverUI && IsPointerOverUI()) return;

            if (peg == null) ClearSelection();       // click outside the board cancels
            else HandlePegClicked(peg);
        }

        /// <summary>
        /// Draw the band by dragging across the pegs, the way you would stretch a real one.
        /// </summary>
        /// <remarks>
        /// Tapping four small pegs in a row is genuinely awkward on a phone: each one is a separate
        /// chance to miss, and a miss clears the whole selection. Dragging is one continuous gesture that
        /// a finger is good at, and the band follows as it goes so a mistake is visible before it counts.
        /// <para>
        /// Dragging back onto the previous peg removes the last one, so a wrong turn is undone by
        /// reversing rather than by lifting and starting over.
        /// </para>
        /// </remarks>
        private void UpdateDrag(Peg peg, PointerPhase phase)
        {
            switch (phase)
            {
                case PointerPhase.Began:
                    if (blockWhenPointerOverUI && IsPointerOverUI()) return;

                    ClearSelection();

                    // Starting on empty board is a legitimate way to cancel, so the drag only begins
                    // when a peg is actually under the finger.
                    if (peg == null) return;

                    _dragging = true;
                    TryExtend(peg);
                    return;

                case PointerPhase.Held:
                    if (_dragging && peg != null) TryExtend(peg);
                    return;

                case PointerPhase.Ended:
                    if (!_dragging) return;

                    _dragging = false;

                    // An incomplete run is abandoned rather than left on screen: the player let go, and
                    // a half-drawn band that survives the gesture is just confusing.
                    if (_selection.Count < flowController.Validator.RequiredPegCount) ClearSelection();
                    return;
            }
        }

        /// <summary>
        /// Adds a peg the drag has reached, backtracks over the previous one, or ignores it.
        /// </summary>
        private void TryExtend(Peg peg)
        {
            if (_selection.Count > 0 && _selection[_selection.Count - 1] == peg) return;   // still on it

            // Reversing onto the peg before last: undo rather than refuse.
            if (_selection.Count > 1 && _selection[_selection.Count - 2] == peg)
            {
                RemoveLast();
                return;
            }

            if (_selection.Contains(peg)) return;
            if (!_legalNextPegs.Contains(peg)) return;   // silent: a drag brushes past pegs constantly

            _selection.Add(peg);
            GameEvents.RaiseSelectionChanged(_selection);

            if (_selection.Count < flowController.Validator.RequiredPegCount)
            {
                RefreshLegalNextPegs();
                return;
            }

            // Complete. Submitting here rather than on release makes the band snap into place under the
            // finger, which is the moment it is obvious the gesture worked.
            _dragging = false;

            bool accepted = flowController.SubmitBandSelection(_selection);
            ClearSelection();

            if (!accepted) RefreshLegalNextPegs();
        }

        /// <summary>Where the pointer is in its press, if it is pressed at all.</summary>
        private enum PointerPhase
        {
            /// <summary>Present but not pressed - a hovering mouse.</summary>
            None,

            Began,
            Held,
            Ended
        }

        /// <summary>
        /// Reads position and press state from touch when a finger is down, otherwise from the mouse.
        /// </summary>
        private bool TryGetPointer(out Vector3 position, out PointerPhase phase)
        {
            if (Input.touchSupported && Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                position = touch.position;

                phase = touch.phase switch
                {
                    TouchPhase.Began => PointerPhase.Began,
                    TouchPhase.Moved or TouchPhase.Stationary => PointerPhase.Held,
                    _ => PointerPhase.Ended
                };

                return true;
            }

            if (!Input.mousePresent)
            {
                position = Vector3.zero;
                phase = PointerPhase.None;
                return false;
            }

            position = Input.mousePosition;

            if (Input.GetMouseButtonDown(0)) phase = PointerPhase.Began;
            else if (Input.GetMouseButtonUp(0)) phase = PointerPhase.Ended;
            else if (Input.GetMouseButton(0)) phase = PointerPhase.Held;
            else phase = PointerPhase.None;

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
            _dragging = false;
            _selection.Clear();
            RefreshLegalNextPegs();
            GameEvents.RaiseSelectionChanged(_selection);
        }

        private void HandleGameReset()
        {
            _dragging = false;
            _selection.Clear();
            _legalNextPegs.Clear();
            SetHovered(null);
            GameEvents.RaiseSelectionChanged(_selection);
        }

        private void HandleGameOver(GameResult result)
        {
            _dragging = false;
            _selection.Clear();
            _legalNextPegs.Clear();
            SetHovered(null);
            GameEvents.RaiseSelectionChanged(_selection);
        }
    }
}

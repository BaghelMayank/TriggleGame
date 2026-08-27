using System;
using Triggle.Core;
using Triggle.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Triggle.UI
{
    /// <summary>
    /// The game lobby: player count, per-seat name and colour, series length, and START GAME.
    /// </summary>
    /// <remarks>
    /// Colour picking enforces uniqueness through <see cref="PlayerProfiles.SetColorIndex"/>: choosing a
    /// colour another seat holds swaps the two, so no two players ever share a colour and no seat is
    /// left without one.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class LobbyController : MonoBehaviour
    {
        /// <summary>One seat's row: avatar, name entry and four colour swatches.</summary>
        [Serializable]
        public sealed class SeatRow
        {
            [Tooltip("Row container, hidden for seats not in play.")]
            public GameObject root;

            [Tooltip("Outline image, tinted to the seat's chosen colour.")]
            public Image outline;

            [Tooltip("Emblem, tinted to the seat's chosen colour.")]
            public Image avatar;

            [Tooltip("Name entry. Its placeholder shows the colour's default name.")]
            public TMP_InputField nameInput;

            [Tooltip("Toggles the seat between a person and the computer.")]
            public Button kindButton;

            [Tooltip("Label on the kind toggle, reading HUMAN or CPU.")]
            public TMP_Text kindLabel;

            [Tooltip("Four colour swatch buttons, in palette order.")]
            public Button[] colorButtons = new Button[PlayerProfiles.ColorSlotCount];

            [Tooltip("Selection rings shown on the active swatch.")]
            public GameObject[] colorSelectionMarkers = new GameObject[PlayerProfiles.ColorSlotCount];
        }

        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;
        [SerializeField] private MatchController matchController;
        [SerializeField] private PlayerColorPalette palette;
        [SerializeField] private MainMenuController mainMenu;

        [Header("Player Count")]
        [Tooltip("Buttons for 2, 3 and 4 players, in that order.")]
        [SerializeField] private Button[] playerCountButtons = new Button[3];

        [SerializeField] private Image[] playerCountOutlines = new Image[3];
        [SerializeField] private TMP_Text[] playerCountLabels = new TMP_Text[3];

        [Header("Seats")]
        [SerializeField] private SeatRow[] seatRows = new SeatRow[4];

        [Header("Computer Opponent")]
        [Tooltip("Row holding the difficulty stepper, hidden while every seat in play is a person.")]
        [SerializeField] private GameObject difficultyRoot;

        [SerializeField] private Button difficultyDownButton;
        [SerializeField] private Button difficultyUpButton;
        [SerializeField] private TMP_Text difficultyValueLabel;
        [SerializeField] private TMP_Text difficultyCaptionLabel;

        [Header("Rounds")]
        [SerializeField] private Button roundsDownButton;
        [SerializeField] private Button roundsUpButton;
        [SerializeField] private TMP_Text roundsValueLabel;
        [SerializeField] private TMP_Text roundsCaptionLabel;

        [Header("Actions")]
        [SerializeField] private Button startButton;
        [SerializeField] private Button backButton;

        [Header("Appearance")]
        [SerializeField] private Color selectedOutline = new Color(0.20f, 0.95f, 0.90f);
        [SerializeField] private Color unselectedOutline = new Color(0.98f, 0.45f, 0.42f);
        [SerializeField] private Color selectedText = Color.white;
        [SerializeField] private Color unselectedText = new Color(0.72f, 0.76f, 0.84f);

        [Tooltip("Tint of the CPU tag on a computer-controlled seat.")]
        [SerializeField] private Color computerSeatText = new Color(1.00f, 0.80f, 0.28f);

        private int _playerCount = 2;
        private int _roundCount = 1;

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
            if (matchController == null) matchController = FindObjectOfType<MatchController>();
            if (palette == null) palette = PlayerColorPalette.Fallback;
            if (mainMenu == null) mainMenu = FindObjectOfType<MainMenuController>();

            if (startButton != null) startButton.onClick.AddListener(HandleStartClicked);
            if (backButton != null) backButton.onClick.AddListener(HandleBackClicked);
            if (roundsDownButton != null) roundsDownButton.onClick.AddListener(() => StepRounds(-1));
            if (roundsUpButton != null) roundsUpButton.onClick.AddListener(() => StepRounds(+1));
            if (difficultyDownButton != null) difficultyDownButton.onClick.AddListener(() => StepDifficulty(-1));
            if (difficultyUpButton != null) difficultyUpButton.onClick.AddListener(() => StepDifficulty(+1));

            for (int i = 0; i < playerCountButtons.Length; i++)
            {
                int count = i + 2;
                if (playerCountButtons[i] != null)
                    playerCountButtons[i].onClick.AddListener(() => SetPlayerCount(count));
            }

            // Colour swatches and the human/CPU toggle: capture seat and slot per listener.
            for (int seat = 0; seat < seatRows.Length; seat++)
            {
                SeatRow row = seatRows[seat];
                if (row == null) continue;

                int capturedRowSeat = seat;
                if (row.kindButton != null)
                    row.kindButton.onClick.AddListener(() => ToggleSeatKind(capturedRowSeat));

                if (row.colorButtons == null) continue;

                for (int slot = 0; slot < row.colorButtons.Length; slot++)
                {
                    int capturedSeat = seat;
                    int capturedSlot = slot;
                    Button button = row.colorButtons[slot];

                    if (button != null)
                        button.onClick.AddListener(() => ChooseColor(capturedSeat, capturedSlot));
                }
            }
        }

        private void OnDestroy()
        {
            if (startButton != null) startButton.onClick.RemoveAllListeners();
            if (backButton != null) backButton.onClick.RemoveAllListeners();
            if (roundsDownButton != null) roundsDownButton.onClick.RemoveAllListeners();
            if (roundsUpButton != null) roundsUpButton.onClick.RemoveAllListeners();
            if (difficultyDownButton != null) difficultyDownButton.onClick.RemoveAllListeners();
            if (difficultyUpButton != null) difficultyUpButton.onClick.RemoveAllListeners();

            for (int i = 0; i < playerCountButtons.Length; i++)
                if (playerCountButtons[i] != null) playerCountButtons[i].onClick.RemoveAllListeners();

            for (int seat = 0; seat < seatRows.Length; seat++)
            {
                SeatRow row = seatRows[seat];
                if (row == null) continue;

                if (row.kindButton != null) row.kindButton.onClick.RemoveAllListeners();
                if (row.colorButtons == null) continue;

                for (int slot = 0; slot < row.colorButtons.Length; slot++)
                    if (row.colorButtons[slot] != null) row.colorButtons[slot].onClick.RemoveAllListeners();
            }
        }

        /// <summary>Repopulates every control from stored preferences. Call when the lobby opens.</summary>
        public void Refresh()
        {
            PlayerProfiles.Load();
            SeatRoster.Load();

            SetPlayerCount(_playerCount);
            SetRoundCount(_roundCount);
            RefreshSeatRows();
        }

        // ------------------------------------------------------------------ player count

        /// <summary>Selects how many seats play, showing exactly that many rows.</summary>
        public void SetPlayerCount(int count)
        {
            _playerCount = Mathf.Clamp(count, 2, 4);

            for (int i = 0; i < seatRows.Length; i++)
            {
                SeatRow row = seatRows[i];
                if (row?.root != null) row.root.SetActive(i < _playerCount);
            }

            for (int i = 0; i < playerCountButtons.Length; i++)
            {
                bool selected = i + 2 == _playerCount;

                if (i < playerCountOutlines.Length && playerCountOutlines[i] != null)
                    playerCountOutlines[i].color = selected ? selectedOutline : unselectedOutline;

                if (i < playerCountLabels.Length && playerCountLabels[i] != null)
                    playerCountLabels[i].color = selected ? selectedText : unselectedText;
            }

            // Seats that just left play may have been the only computer ones.
            RefreshDifficultyRow();
        }

        // ------------------------------------------------------------------ computer opponent

        public void StepDifficulty(int delta) =>
            SetDifficulty((AiDifficulty)((int)SeatRoster.Difficulty + delta));

        /// <summary>Sets the level every computer seat plays at.</summary>
        public void SetDifficulty(AiDifficulty difficulty)
        {
            SeatRoster.Difficulty = difficulty;
            RefreshDifficultyRow();
        }

        /// <summary>
        /// Updates the difficulty stepper and hides the whole row when no seat in play is a computer -
        /// a difficulty control in a hot-seat game would set an expectation the match never meets.
        /// </summary>
        private void RefreshDifficultyRow()
        {
            bool anyComputer = SeatRoster.ComputerSeatCount(_playerCount) > 0;
            if (difficultyRoot != null) difficultyRoot.SetActive(anyComputer);

            AiDifficulty difficulty = SeatRoster.Difficulty;

            if (difficultyValueLabel != null)
                difficultyValueLabel.text = SeatRoster.DifficultyName(difficulty).ToUpperInvariant();

            if (difficultyCaptionLabel != null)
                difficultyCaptionLabel.text = SeatRoster.DifficultyCaption(difficulty);

            if (difficultyDownButton != null)
                difficultyDownButton.interactable = difficulty > AiDifficulty.Easy;

            if (difficultyUpButton != null)
                difficultyUpButton.interactable = difficulty < AiDifficulty.Hard;
        }

        /// <summary>
        /// Flips a seat between a person and the computer. Typed names are committed first for the same
        /// reason as a colour swap: the row is about to be rebuilt from stored state.
        /// </summary>
        private void ToggleSeatKind(int seatIndex)
        {
            CommitNames();

            SeatRoster.ToggleKind((PlayerId)(seatIndex + 1));

            RefreshSeatRows();
            RefreshDifficultyRow();
        }

        // ------------------------------------------------------------------ rounds

        public void StepRounds(int delta) => SetRoundCount(_roundCount + delta);

        /// <summary>Sets how many rounds the match runs for (1-10).</summary>
        public void SetRoundCount(int rounds)
        {
            _roundCount = Mathf.Clamp(rounds, MatchState.MinRounds, MatchState.MaxRounds);

            if (roundsValueLabel != null) roundsValueLabel.text = _roundCount.ToString();

            if (roundsCaptionLabel != null)
            {
                roundsCaptionLabel.text = _roundCount == 1
                    ? "Single round - no round counter"
                    : $"Best of {_roundCount} - most rounds won takes the match";
            }

            if (roundsDownButton != null) roundsDownButton.interactable = _roundCount > MatchState.MinRounds;
            if (roundsUpButton != null) roundsUpButton.interactable = _roundCount < MatchState.MaxRounds;
        }

        // ------------------------------------------------------------------ seats

        /// <summary>Pushes each seat's stored name and colour onto its row.</summary>
        private void RefreshSeatRows()
        {
            for (int i = 0; i < seatRows.Length; i++)
            {
                SeatRow row = seatRows[i];
                if (row == null) continue;

                var player = (PlayerId)(i + 1);
                int slot = PlayerProfiles.GetColorIndex(player);
                Color color = palette.GetColorBySlot(slot);
                bool isComputer = SeatRoster.IsComputer(player);

                if (row.outline != null) row.outline.color = color;
                if (row.avatar != null) row.avatar.color = color;

                if (row.kindLabel != null)
                {
                    row.kindLabel.text = isComputer ? "CPU" : "HUMAN";
                    row.kindLabel.color = isComputer ? computerSeatText : unselectedText;
                }

                if (row.nameInput != null)
                {
                    row.nameInput.characterLimit = PlayerProfiles.MaxNameLength;

                    // A computer seat is named after its colour, so the field is emptied and locked
                    // rather than showing a typed name the HUD will not use.
                    row.nameInput.interactable = !isComputer;
                    row.nameInput.SetTextWithoutNotify(
                        isComputer ? string.Empty : PlayerProfiles.GetRawName(player));

                    if (row.nameInput.placeholder is TMP_Text placeholder)
                    {
                        placeholder.text = isComputer
                            ? $"{palette.GetColorName(slot)} (CPU)"
                            : palette.GetColorName(slot);

                        placeholder.color = new Color(color.r, color.g, color.b, 0.45f);
                    }
                }

                // Swatch colours are fixed; only the selection ring moves.
                if (row.colorButtons != null)
                {
                    for (int s = 0; s < row.colorButtons.Length; s++)
                    {
                        Button button = row.colorButtons[s];
                        if (button != null && button.targetGraphic != null)
                            button.targetGraphic.color = palette.GetColorBySlot(s);
                    }
                }

                if (row.colorSelectionMarkers != null)
                {
                    for (int s = 0; s < row.colorSelectionMarkers.Length; s++)
                        if (row.colorSelectionMarkers[s] != null)
                            row.colorSelectionMarkers[s].SetActive(s == slot);
                }
            }
        }

        /// <summary>
        /// Assigns a colour to a seat. Swaps with whoever already holds it, then refreshes every row so
        /// both sides of the swap update.
        /// </summary>
        private void ChooseColor(int seatIndex, int colorSlot)
        {
            var player = (PlayerId)(seatIndex + 1);

            // Commit typed names first: swapping colours changes placeholders, and re-reading the
            // fields afterwards would otherwise discard anything mid-edit.
            CommitNames();

            PlayerProfiles.SetColorIndex(player, colorSlot);
            RefreshSeatRows();
        }

        /// <summary>Persists whatever is currently typed. Blank fields fall back to the colour name.</summary>
        /// <remarks>
        /// Computer seats are skipped rather than committed. Their field is deliberately blank, so
        /// writing it back would erase the name the person had typed there before the seat was handed to
        /// the CPU - and toggling the seat back to human would find it gone.
        /// </remarks>
        private void CommitNames()
        {
            for (int i = 0; i < seatRows.Length; i++)
            {
                SeatRow row = seatRows[i];
                if (row?.nameInput == null) continue;

                var player = (PlayerId)(i + 1);
                if (SeatRoster.IsComputer(player)) continue;

                PlayerProfiles.SetName(player, row.nameInput.text);
            }
        }

        // ------------------------------------------------------------------ actions

        private void HandleStartClicked()
        {
            if (flowController == null)
            {
                Debug.LogError($"{nameof(LobbyController)}: no {nameof(GameFlowController)} assigned.", this);
                return;
            }

            CommitNames();
            EnsureComputerSeatsCanPlay();

            // Seat count must be applied before the match starts: MatchState seeds its per-player
            // tallies from the active seat list.
            flowController.ConfigurePlayerCount(_playerCount);

            if (matchController != null)
            {
                matchController.ConfigureRounds(_roundCount);
                matchController.StartMatch();
            }
            else
            {
                flowController.StartNewGame();
            }

            if (mainMenu != null) mainMenu.EnterGame();
        }

        /// <summary>
        /// Falls back to hot-seat when the scene has no <see cref="AiController"/> to play the computer
        /// seats, which is the state a scene generated before the AI existed is in.
        /// </summary>
        /// <remarks>
        /// Without this the match would simply stop on the first computer turn: the flow controller
        /// would sit in <c>WaitingForInput</c> with board input closed because the seat is automated,
        /// and nothing in the scene would ever submit a move. A silent hang is the worst possible
        /// symptom for a missing component, so it degrades to a playable game and says why.
        /// </remarks>
        private void EnsureComputerSeatsCanPlay()
        {
            if (SeatRoster.ComputerSeatCount(_playerCount) == 0) return;
            if (FindObjectOfType<AiController>() != null) return;

            Debug.LogWarning(
                $"{nameof(LobbyController)}: this scene has no {nameof(AiController)}, so computer seats " +
                "cannot play. Starting as hot-seat instead - re-run Tools > Triggle > Build Play Scene " +
                "to add it.", this);

            SeatRoster.SetAllHuman();
            RefreshSeatRows();
            RefreshDifficultyRow();
        }

        private void HandleBackClicked()
        {
            CommitNames();
            if (mainMenu != null) mainMenu.ShowRootMenu();
        }

        private void OnValidate()
        {
            EnsureLength(ref seatRows, 4);
            EnsureLength(ref playerCountButtons, 3);
            EnsureLength(ref playerCountOutlines, 3);
            EnsureLength(ref playerCountLabels, 3);

            if (seatRows == null) return;

            for (int i = 0; i < seatRows.Length; i++)
            {
                if (seatRows[i] == null) continue;

                EnsureLength(ref seatRows[i].colorButtons, PlayerProfiles.ColorSlotCount);
                EnsureLength(ref seatRows[i].colorSelectionMarkers, PlayerProfiles.ColorSlotCount);
            }
        }

        private static void EnsureLength<T>(ref T[] array, int length) where T : class
        {
            if (array != null && array.Length == length) return;

            var resized = new T[length];
            for (int i = 0; i < length; i++)
                resized[i] = array != null && i < array.Length ? array[i] : null;

            array = resized;
        }
    }
}

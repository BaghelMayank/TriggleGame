using System;
using UnityEngine;

namespace Triggle.Core
{
    /// <summary>Who sits in a seat: a person at the keyboard, or the computer.</summary>
    public enum SeatKind
    {
        Human = 0,
        Computer = 1
    }

    /// <summary>How hard the computer plays. See <c>BandEvaluator</c> for what each level actually does.</summary>
    public enum AiDifficulty
    {
        Easy = 0,
        Normal = 1,
        Hard = 2
    }

    /// <summary>
    /// Runtime store for which seats are played by the computer, persisted with <see cref="PlayerPrefs"/>
    /// so a lineup survives between sessions.
    /// </summary>
    /// <remarks>
    /// Deliberately parallel to <see cref="PlayerProfiles"/>, which owns names and colours: a seat's
    /// identity and a seat's controller are separate choices, and mixing them would mean the lobby had to
    /// rewrite a player's name every time it toggled them to CPU.
    /// <para>
    /// Difficulty is a single match-wide setting rather than per-seat. Three computer opponents each at a
    /// different level is a configuration nobody asked for, and it makes the result unreadable - you can
    /// no longer tell whether you beat "the AI" or just the weak one.
    /// </para>
    /// </remarks>
    public static class SeatRoster
    {
        private const string KindKeyPrefix = "triggle.seat.kind.";
        private const string DifficultyKey = "triggle.ai.difficulty";

        /// <summary>Seats the game supports, matching <see cref="PlayerId"/>.</summary>
        public const int SeatCount = 4;

        private static readonly SeatKind[] Kinds = new SeatKind[SeatCount];
        private static AiDifficulty _difficulty = AiDifficulty.Normal;
        private static bool _loaded;

        /// <summary>Raised whenever a seat's controller or the difficulty changes.</summary>
        public static event Action OnRosterChanged;

        /// <summary>Loads the stored lineup. Safe to call repeatedly; only the first call does work.</summary>
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            for (int seat = 1; seat <= SeatCount; seat++)
            {
                int stored = PlayerPrefs.GetInt(KindKeyPrefix + seat, (int)SeatKind.Human);
                Kinds[seat - 1] = stored == (int)SeatKind.Computer ? SeatKind.Computer : SeatKind.Human;
            }

            int difficulty = PlayerPrefs.GetInt(DifficultyKey, (int)AiDifficulty.Normal);
            _difficulty = (AiDifficulty)Mathf.Clamp(difficulty, (int)AiDifficulty.Easy, (int)AiDifficulty.Hard);
        }

        /// <summary>Skill level every computer seat plays at.</summary>
        public static AiDifficulty Difficulty
        {
            get { Load(); return _difficulty; }
            set
            {
                Load();
                AiDifficulty clamped = (AiDifficulty)Mathf.Clamp(
                    (int)value, (int)AiDifficulty.Easy, (int)AiDifficulty.Hard);

                if (clamped == _difficulty) return;

                _difficulty = clamped;
                PlayerPrefs.SetInt(DifficultyKey, (int)clamped);
                PlayerPrefs.Save();

                OnRosterChanged?.Invoke();
            }
        }

        /// <summary>Human-readable difficulty name, for the lobby label.</summary>
        public static string DifficultyName(AiDifficulty difficulty) => difficulty switch
        {
            AiDifficulty.Easy => "Easy",
            AiDifficulty.Hard => "Hard",
            _ => "Normal"
        };

        /// <summary>One line describing how the level plays, shown under the difficulty stepper.</summary>
        public static string DifficultyCaption(AiDifficulty difficulty) => difficulty switch
        {
            AiDifficulty.Easy => "Plays loosely and often hands you a triangle",
            AiDifficulty.Hard => "Looks a move ahead and refuses to set you up",
            _ => "Takes what is on offer and avoids the obvious gift"
        };

        public static SeatKind GetKind(PlayerId player)
        {
            if (player == PlayerId.None) return SeatKind.Human;

            Load();
            return Kinds[SeatIndex(player)];
        }

        /// <summary>True when the computer plays this seat.</summary>
        public static bool IsComputer(PlayerId player) => GetKind(player) == SeatKind.Computer;

        public static void SetKind(PlayerId player, SeatKind kind)
        {
            if (player == PlayerId.None) return;

            Load();
            int index = SeatIndex(player);
            if (Kinds[index] == kind) return;

            Kinds[index] = kind;
            PlayerPrefs.SetInt(KindKeyPrefix + (int)player, (int)kind);
            PlayerPrefs.Save();

            OnRosterChanged?.Invoke();
        }

        /// <summary>Flips a seat between human and computer.</summary>
        public static void ToggleKind(PlayerId player) =>
            SetKind(player, IsComputer(player) ? SeatKind.Human : SeatKind.Computer);

        /// <summary>Number of computer seats among the first <paramref name="playerCount"/> of them.</summary>
        public static int ComputerSeatCount(int playerCount)
        {
            Load();
            int count = 0;
            int seats = Mathf.Clamp(playerCount, 0, SeatCount);

            for (int i = 0; i < seats; i++)
                if (Kinds[i] == SeatKind.Computer) count++;

            return count;
        }

        /// <summary>True when at least one of the seats in play is a person. Used to label the lobby.</summary>
        public static bool HasHumanSeat(int playerCount) =>
            ComputerSeatCount(playerCount) < Mathf.Clamp(playerCount, 0, SeatCount);

        /// <summary>Hot-seat lineup: everyone at the table is a person.</summary>
        public static void SetAllHuman()
        {
            Load();
            bool changed = false;

            for (int seat = 1; seat <= SeatCount; seat++)
            {
                if (Kinds[seat - 1] == SeatKind.Human) continue;

                Kinds[seat - 1] = SeatKind.Human;
                PlayerPrefs.SetInt(KindKeyPrefix + seat, (int)SeatKind.Human);
                changed = true;
            }

            if (!changed) return;

            PlayerPrefs.Save();
            OnRosterChanged?.Invoke();
        }

        /// <summary>
        /// Single-player lineup: seat 1 is the person, every other seat is the computer.
        /// </summary>
        /// <remarks>
        /// Seat 1 specifically, because <c>MatchController.localPlayer</c> writes the Win / Lose panel
        /// from that seat's point of view. Making any other seat the human would show "You lose" when
        /// they had in fact won.
        /// </remarks>
        public static void SetSinglePlayerLineup()
        {
            Load();
            bool changed = false;

            for (int seat = 1; seat <= SeatCount; seat++)
            {
                SeatKind kind = seat == 1 ? SeatKind.Human : SeatKind.Computer;
                if (Kinds[seat - 1] == kind) continue;

                Kinds[seat - 1] = kind;
                PlayerPrefs.SetInt(KindKeyPrefix + seat, (int)kind);
                changed = true;
            }

            if (!changed) return;

            PlayerPrefs.Save();
            OnRosterChanged?.Invoke();
        }

        /// <summary>
        /// Appends a "(CPU)" tag to a computer seat's name, so the HUD and standings show at a glance
        /// which scores are yours. Human seats are returned untouched.
        /// </summary>
        public static string Decorate(PlayerId player, string baseName) =>
            IsComputer(player) ? baseName + " (CPU)" : baseName;

        private static int SeatIndex(PlayerId player) =>
            Mathf.Clamp((int)player - 1, 0, SeatCount - 1);
    }
}

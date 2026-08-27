using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Triggle.Core;
using Triggle.Gameplay;
using Triggle.Grid;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Headless self-play harness for the computer opponent: plays whole rounds with no scene, no
    /// coroutines and no rendering, then reports whether the difficulty ladder actually holds.
    /// </summary>
    /// <remarks>
    /// The point is not that the AI wins - it is that the levels are <b>ordered</b>. A difficulty
    /// setting that does not change the outcome is worse than no setting at all, because the player
    /// changes it, loses anyway, and concludes the game is broken. Run this after touching
    /// <see cref="BandEvaluator"/>'s weights.
    /// <para>
    /// Move application below deliberately mirrors <c>GameFlowController.ResolveMoveRoutine</c> minus
    /// its animation waits. Every chosen band is also pushed through the real
    /// <see cref="MoveValidator"/>, so if the evaluator ever picks a move the rules engine would refuse,
    /// this reports it rather than quietly playing an illegal game.
    /// </para>
    /// </remarks>
    public static class TriggleAiVerification
    {
        private const int GamesPerMatchup = 60;
        private const int BoardRadius = 3;

        /// <summary>A policy under test: a difficulty level, or the random baseline.</summary>
        private enum Policy
        {
            Random,
            Easy,
            Normal,
            Hard
        }

        [MenuItem("Tools/Triggle/Verify AI (self-play)", false, 40)]
        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine($"[Triggle] AI self-play verification - R={BoardRadius}, " +
                              $"{GamesPerMatchup} games per matchup.");

            var stopwatch = Stopwatch.StartNew();

            GameObject host = null;
            try
            {
                host = new GameObject("~TriggleAiVerification") { hideFlags = HideFlags.HideAndDontSave };
                var board = host.AddComponent<BoardManager>();
                board.SetRadius(BoardRadius);
                board.Build();

                var settings = new GameSettings { playerCount = 2, requireAtLeastOneNewEdge = true };
                var validator = new MoveValidator(board, settings);
                var evaluator = new BandEvaluator(board, settings);

                int illegalMoves = 0;
                int incompleteBoards = 0;

                report.AppendLine();
                report.AppendLine("  Matchup                  P1 wins   draws   P2 wins   avg score");
                report.AppendLine("  -------------------------------------------------------------");

                RunMatchup(board, validator, evaluator, Policy.Normal, Policy.Random,
                           report, ref illegalMoves, ref incompleteBoards);
                RunMatchup(board, validator, evaluator, Policy.Normal, Policy.Easy,
                           report, ref illegalMoves, ref incompleteBoards);
                RunMatchup(board, validator, evaluator, Policy.Hard, Policy.Normal,
                           report, ref illegalMoves, ref incompleteBoards);

                stopwatch.Stop();

                report.AppendLine();
                report.AppendLine($"  Illegal moves chosen:   {illegalMoves}");
                report.AppendLine($"  Boards left unfinished: {incompleteBoards}");
                report.AppendLine($"  Elapsed:                {stopwatch.ElapsedMilliseconds} ms");

                if (illegalMoves > 0)
                    Debug.LogError(report + "\nThe evaluator and the validator disagree about legality.");
                else if (incompleteBoards > 0)
                    Debug.LogError(report + "\nA round ended with triangles still unclaimed.");
                else
                    Debug.Log(report.ToString());
            }
            finally
            {
                if (host != null) Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Plays a full set of games between two policies, alternating who moves first so the reported
        /// win rate measures the policies rather than the first-move advantage.
        /// </summary>
        private static void RunMatchup(BoardManager board, MoveValidator validator, BandEvaluator evaluator,
                                        Policy first, Policy second, StringBuilder report,
                                        ref int illegalMoves, ref int incompleteBoards)
        {
            int firstWins = 0;
            int secondWins = 0;
            int draws = 0;
            int firstScoreTotal = 0;

            for (int game = 0; game < GamesPerMatchup; game++)
            {
                // Swapping seats on odd games keeps the sample balanced.
                bool swapped = (game & 1) == 1;
                Policy seat1 = swapped ? second : first;
                Policy seat2 = swapped ? first : second;

                PlayRound(board, validator, evaluator, seat1, seat2,
                          out int score1, out int score2, ref illegalMoves);

                if (board.CountClaimedCells() < board.TotalCells) incompleteBoards++;

                int firstScore = swapped ? score2 : score1;
                int secondScore = swapped ? score1 : score2;

                firstScoreTotal += firstScore;

                if (firstScore > secondScore) firstWins++;
                else if (secondScore > firstScore) secondWins++;
                else draws++;
            }

            float averageScore = firstScoreTotal / (float)GamesPerMatchup;
            string label = $"{first} vs {second}";

            report.AppendLine($"  {label,-22} {firstWins,7} {draws,7} {secondWins,9} {averageScore,11:0.0}");
        }

        /// <summary>
        /// Plays one board to exhaustion and returns each seat's triangle count.
        /// </summary>
        private static void PlayRound(BoardManager board, MoveValidator validator, BandEvaluator evaluator,
                                       Policy seat1, Policy seat2, out int score1, out int score2,
                                       ref int illegalMoves)
        {
            board.ResetRuntimeState();

            score1 = 0;
            score2 = 0;

            PlayerId current = PlayerId.Player1;

            // Bounded rather than while(true): every band can be placed at most once, so a round cannot
            // legitimately outlast the catalogue. Exceeding it would mean the loop is not converging.
            int moveLimit = board.Bands.Count + 1;

            for (int move = 0; move < moveLimit; move++)
            {
                if (!validator.HasAnyLegalMove()) break;

                Policy policy = current == PlayerId.Player1 ? seat1 : seat2;
                BandPlacement band = Choose(board, validator, evaluator, policy);
                if (band == null) break;

                if (!validator.IsBandLegal(band, out _))
                {
                    illegalMoves++;
                    break;
                }

                int claimed = ApplyBand(board, band, current);

                if (current == PlayerId.Player1) score1 += claimed;
                else score2 += claimed;

                // The turn always passes, scoring or not - the same rule the flow controller applies.
                current = current == PlayerId.Player1 ? PlayerId.Player2 : PlayerId.Player1;
            }
        }

        private static BandPlacement Choose(BoardManager board, MoveValidator validator,
                                             BandEvaluator evaluator, Policy policy)
        {
            if (policy != Policy.Random)
                return evaluator.ChooseBand((AiDifficulty)((int)policy - 1));

            // Baseline: uniform over the legal catalogue, with no idea what a triangle is.
            var legal = new List<BandPlacement>(board.Bands.Count);
            IReadOnlyList<BandPlacement> all = board.Bands;

            for (int i = 0; i < all.Count; i++)
                if (validator.IsBandLegal(all[i], out _)) legal.Add(all[i]);

            return legal.Count == 0 ? null : legal[Random.Range(0, legal.Count)];
        }

        /// <summary>
        /// Commits a band and claims whatever it encloses. Mirrors the flow controller's
        /// <c>ApplyBand</c> plus <c>CollectAffectedCells</c> resolve step.
        /// </summary>
        private static int ApplyBand(BoardManager board, BandPlacement band, PlayerId player)
        {
            band.IsPlaced = true;
            band.PlacedBy = player;

            for (int i = 0; i < band.Edges.Length; i++)
            {
                BoardEdge edge = band.Edges[i];
                edge.BandCount++;

                if (edge.IsOccupied) continue;

                edge.IsOccupied = true;
                edge.FirstCoveredBy = player;
            }

            var seen = new HashSet<TriangleCell>();
            int claimed = 0;

            for (int i = 0; i < band.Edges.Length; i++)
            {
                List<TriangleCell> cells = band.Edges[i].Cells;

                for (int c = 0; c < cells.Count; c++)
                {
                    TriangleCell cell = cells[c];
                    if (cell == null || !seen.Add(cell)) continue;
                    if (cell.IsClaimed || !cell.IsFullyEnclosed) continue;

                    cell.Owner = player;
                    claimed++;
                }
            }

            return claimed;
        }
    }
}

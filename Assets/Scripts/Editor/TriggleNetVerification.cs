using System.Collections.Generic;
using System.Text;
using Triggle.Core;
using Triggle.Gameplay;
using Triggle.Grid;
using Triggle.Net;
using UnityEditor;
using UnityEngine;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Checks the assumption the whole multiplayer design rests on: that a band index means the same
    /// three edges on every device, so replaying a sequence of them reproduces an identical board.
    /// </summary>
    /// <remarks>
    /// If that holds, a turn is one integer and there is nothing to reconcile. If it does not, two
    /// players see different boards and neither is told - the worst possible failure, because it looks
    /// like a working game right up until the scores disagree. It is worth proving before a line of
    /// networking code is written, which is why this runs with no transport, no service account and no
    /// sockets.
    /// <para>
    /// <b>Not covered here:</b> <see cref="NetworkMatch"/> itself. It drives
    /// <see cref="GameFlowController"/>, whose move resolution is a coroutine, and coroutines do not run
    /// in edit mode - so the binding needs a play-mode test rather than this.
    /// </para>
    /// </remarks>
    public static class TriggleNetVerification
    {
        private const int GamesPerRadius = 40;

        [MenuItem("Tools/Triggle/Verify Multiplayer Spine", false, 43)]
        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine("[Triggle] Multiplayer spine verification.");

            int failures = 0;

            failures += CheckProtocol(report);
            failures += CheckLoopback(report);
            failures += CheckSeatAllocation(report);
            failures += CheckCatalogueDeterminism(report);
            failures += CheckReplayConvergence(report);

            report.AppendLine();
            report.AppendLine($"  Failures: {failures}");

            if (failures > 0) Debug.LogError(report.ToString());
            else Debug.Log(report.ToString());
        }

        // ================================================================== protocol

        private static int CheckProtocol(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("  Wire format");
            report.AppendLine("  -----------");

            int failures = 0;

            var cases = new List<NetMessage>
            {
                NetMessage.Hello(2, "Mayank", 3, 998877),
                NetMessage.AssignSeat(998877, 3),
                NetMessage.Chat(2, ChatKind.Emote, 4, null),
                NetMessage.StartMatch(4, 4, 3, 5),
                NetMessage.PlaceBand(1, 173, 42),
                NetMessage.Chat(3, ChatKind.Phrase, 1, "Good game"),
                NetMessage.NextRound(2),
                NetMessage.Resign(4)
            };

            int roundTripped = 0;

            foreach (NetMessage original in cases)
            {
                if (!NetMessage.TryDeserialize(original.Serialize(), out NetMessage decoded))
                {
                    report.AppendLine($"    FAIL {original.Kind} did not survive a round trip");
                    failures++;
                    continue;
                }

                bool same = decoded.Kind == original.Kind && decoded.Seat == original.Seat &&
                            decoded.A == original.A && decoded.B == original.B &&
                            decoded.C == original.C && decoded.D == original.D &&
                            (decoded.Text ?? string.Empty) == (original.Text ?? string.Empty);

                if (same) { roundTripped++; continue; }

                report.AppendLine($"    FAIL {original.Kind} changed: {original} -> {decoded}");
                failures++;
            }

            report.AppendLine($"    {roundTripped}/{cases.Count} messages round-tripped unchanged");

            // Bytes from a network are untrusted, including from a peer on a different build. Every one
            // of these must be refused without throwing.
            var malformed = new List<(string Name, byte[] Data)>
            {
                ("null", null),
                ("empty", new byte[0]),
                ("truncated", new byte[] { 3, 1, 0, 0 }),
                ("zero kind", new NetMessage { Kind = NetMessageKind.None }.Serialize()),
                ("garbage", new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255,
                                         255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 })
            };

            int rejected = 0;

            foreach ((string name, byte[] data) in malformed)
            {
                bool accepted;

                try
                {
                    accepted = NetMessage.TryDeserialize(data, out _);
                }
                catch (System.Exception e)
                {
                    report.AppendLine($"    FAIL malformed '{name}' threw {e.GetType().Name}");
                    failures++;
                    continue;
                }

                if (!accepted) { rejected++; continue; }

                report.AppendLine($"    FAIL malformed '{name}' was accepted");
                failures++;
            }

            report.AppendLine($"    {rejected}/{malformed.Count} malformed packets refused without throwing");

            // The Relay transport refuses to send anything over its packet limit, so a message that
            // could legitimately exceed it would be silently dropped in a live game and never in a test.
            // Worst case is a Hello or Chat carrying a full-length, entirely multi-byte string.
            string widest = new string('é', NetMessage.MaxTextLength);
            int largest = NetMessage.Chat(4, ChatKind.Text, 0, widest).Serialize().Length;

            report.AppendLine($"    largest possible message: {largest} bytes " +
                              $"(transport limit {UgsSessionTransport.MaxPacketSize})");

            if (largest > UgsSessionTransport.MaxPacketSize)
            {
                report.AppendLine("    FAIL a legal message does not fit in one packet");
                failures++;
            }

            return failures;
        }

        // ================================================================== loopback

        private static int CheckLoopback(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("  Loopback transport");
            report.AppendLine("  ------------------");

            int failures = 0;

            var host = new LoopbackTransport(1, true);
            var guest = new LoopbackTransport(2, false);
            LoopbackTransport.Connect(host, guest);

            var atHost = new List<NetMessage>();
            var atGuest = new List<NetMessage>();

            host.MessageReceived += m => atHost.Add(m);
            guest.MessageReceived += m => atGuest.Add(m);

            if (host.State != SessionStatus.Connected || guest.State != SessionStatus.Connected)
            {
                report.AppendLine("    FAIL peers did not reach Connected");
                failures++;
            }

            // Ordering is the property that matters: a move applied out of order diverges the boards.
            for (int i = 0; i < 8; i++) host.Send(NetMessage.PlaceBand(1, i * 3, i));

            host.Poll();
            guest.Poll();

            if (atHost.Count != 0)
            {
                report.AppendLine($"    FAIL sender received its own traffic ({atHost.Count} messages)");
                failures++;
            }

            if (atGuest.Count != 8)
            {
                report.AppendLine($"    FAIL guest received {atGuest.Count} of 8 messages");
                failures++;
            }
            else
            {
                bool ordered = true;
                for (int i = 0; i < 8; i++)
                    if (atGuest[i].A != i * 3 || atGuest[i].B != i) ordered = false;

                if (!ordered)
                {
                    report.AppendLine("    FAIL messages arrived out of order");
                    failures++;
                }
                else
                {
                    report.AppendLine("    8/8 messages delivered in order, none echoed to the sender");
                }
            }

            host.Dispose();
            guest.Dispose();

            return failures;
        }

        // ================================================================== seats

        /// <summary>
        /// Three players where two claim the same seat, which is what happens on a real join race.
        /// </summary>
        /// <remarks>
        /// Guests derive their seat from their own snapshot of the lobby roster, so two devices joining
        /// at nearly the same instant can both read seat 2. The host used to record the first and drop
        /// the second without a word - the player stayed connected, stayed in the lobby, and was
        /// invisible to everyone, which is exactly what a three-device test turned up. The host now
        /// reassigns instead, and this pins that down.
        /// </remarks>
        private static int CheckSeatAllocation(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("  Seat allocation");
            report.AppendLine("  ---------------");

            var hosts = new GameObject("~TriggleSeatHost") { hideFlags = HideFlags.HideAndDontSave };
            var first = new GameObject("~TriggleSeatGuestA") { hideFlags = HideFlags.HideAndDontSave };
            var second = new GameObject("~TriggleSeatGuestB") { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                NetworkMatch host = hosts.AddComponent<NetworkMatch>();
                NetworkMatch guestA = first.AddComponent<NetworkMatch>();
                NetworkMatch guestB = second.AddComponent<NetworkMatch>();

                var hostPipe = new LoopbackTransport(1, true);
                var pipeA = new LoopbackTransport(2, false);

                // The collision: guest B read a stale roster and believes it is also seat 2.
                var pipeB = new LoopbackTransport(2, false);

                LoopbackTransport.Connect(hostPipe, pipeA, pipeB);

                host.Join(hostPipe, "Host", 101);
                guestA.Join(pipeA, "Guest A", 102);
                guestB.Join(pipeB, "Guest B", 103);

                // Several rounds: the correction is a reply, and the re-announcement is another.
                for (int round = 0; round < 6; round++)
                {
                    hostPipe.Poll();
                    pipeA.Poll();
                    pipeB.Poll();
                }

                int failures = 0;

                failures += Expect(report, host.PlayerCount == 3,
                    $"host sees 3 players (saw {host.PlayerCount})");

                failures += Expect(report, guestA.LocalSeat != guestB.LocalSeat,
                    $"the two guests hold different seats (A={guestA.LocalSeat}, B={guestB.LocalSeat})");

                failures += Expect(report, guestB.LocalSeat >= 1 && guestB.LocalSeat <= SeatRoster.SeatCount,
                    $"the reassigned guest has a valid seat ({guestB.LocalSeat})");

                failures += Expect(report, guestA.PlayerCount == 3 && guestB.PlayerCount == 3,
                    $"both guests see everyone (A={guestA.PlayerCount}, B={guestB.PlayerCount})");

                failures += Expect(report,
                    host.NameOfSeat(guestB.LocalSeat) == "Guest B",
                    $"the host has the reassigned guest's name at its new seat " +
                    $"(seat {guestB.LocalSeat} is \"{host.NameOfSeat(guestB.LocalSeat)}\")");

                hostPipe.Dispose();
                pipeA.Dispose();
                pipeB.Dispose();

                return failures;
            }
            finally
            {
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(hosts);
            }
        }

        private static int Expect(StringBuilder report, bool condition, string description)
        {
            report.AppendLine($"    {(condition ? "ok  " : "FAIL")} {description}");
            return condition ? 0 : 1;
        }

        // ================================================================== catalogue

        private static int CheckCatalogueDeterminism(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("  Band catalogue determinism");
            report.AppendLine("  --------------------------");
            report.AppendLine("    Radius   Bands   Catalogue hash   Stable across rebuilds");

            int failures = 0;

            for (int radius = TrigglePrefs.MinBoardRadius; radius <= TrigglePrefs.MaxBoardRadius; radius++)
            {
                GameObject first = null;
                GameObject second = null;

                try
                {
                    // Two independent BoardManagers, exactly as two devices would each build their own.
                    BoardManager a = NewBoard(radius, out first);
                    BoardManager b = NewBoard(radius, out second);

                    long hashA = CatalogueHash(a);
                    long hashB = CatalogueHash(b);

                    // And again after a rebuild in place, which is what a rematch does.
                    a.Build();
                    long hashRebuilt = CatalogueHash(a);

                    bool stable = hashA == hashB && hashA == hashRebuilt;
                    if (!stable) failures++;

                    report.AppendLine($"    {radius,6} {a.Bands.Count,7}   {hashA,14:X}   " +
                                      $"{(stable ? "yes" : "NO - DIVERGED")}");
                }
                finally
                {
                    if (first != null) Object.DestroyImmediate(first);
                    if (second != null) Object.DestroyImmediate(second);
                }
            }

            return failures;
        }

        /// <summary>
        /// Order-sensitive hash of the whole catalogue: index, axis and every peg coordinate.
        /// </summary>
        /// <remarks>
        /// Order-sensitive on purpose. Two devices agreeing on the <i>set</i> of bands is not enough -
        /// they must agree on which index each one has, because the index is what travels.
        /// </remarks>
        private static long CatalogueHash(BoardManager board)
        {
            unchecked
            {
                long hash = 1469598103934665603L;   // FNV-1a 64-bit offset basis

                IReadOnlyList<BandPlacement> bands = board.Bands;
                for (int i = 0; i < bands.Count; i++)
                {
                    BandPlacement band = bands[i];

                    hash = Mix(hash, band.Id);
                    hash = Mix(hash, band.Axis.x);
                    hash = Mix(hash, band.Axis.y);

                    for (int p = 0; p < band.Pegs.Length; p++)
                    {
                        hash = Mix(hash, band.Pegs[p].Coord.x);
                        hash = Mix(hash, band.Pegs[p].Coord.y);
                    }

                    for (int e = 0; e < band.Edges.Length; e++) hash = Mix(hash, band.Edges[e].Id);
                }

                return hash;
            }
        }

        private static long Mix(long hash, int value)
        {
            unchecked
            {
                hash ^= value;
                return hash * 1099511628211L;   // FNV-1a 64-bit prime
            }
        }

        // ================================================================== replay

        private static int CheckReplayConvergence(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("  Move replay convergence");
            report.AppendLine("  -----------------------");
            report.AppendLine("    Radius   Games   Moves relayed   Boards identical   Bytes/move");

            int failures = 0;

            for (int radius = TrigglePrefs.MinBoardRadius; radius <= TrigglePrefs.MaxBoardRadius; radius++)
            {
                GameObject hostGo = null;
                GameObject guestGo = null;

                try
                {
                    BoardManager host = NewBoard(radius, out hostGo);
                    BoardManager guest = NewBoard(radius, out guestGo);

                    var settings = new GameSettings { playerCount = 2, requireAtLeastOneNewEdge = true };
                    var hostRules = new MoveValidator(host, settings);
                    var guestRules = new MoveValidator(guest, settings);
                    var evaluator = new BandEvaluator(host, settings);

                    int mismatches = 0;
                    int totalMoves = 0;
                    int totalBytes = 0;

                    for (int game = 0; game < GamesPerRadius; game++)
                    {
                        host.ResetRuntimeState();
                        guest.ResetRuntimeState();

                        PlayerId turn = PlayerId.Player1;
                        int moveLimit = host.Bands.Count + 1;

                        for (int move = 0; move < moveLimit; move++)
                        {
                            if (!hostRules.HasAnyLegalMove()) break;

                            BandPlacement chosen = evaluator.ChooseBand(AiDifficulty.Normal);
                            if (chosen == null) break;

                            // Everything the other device learns about this turn: one integer, packed
                            // and unpacked through the real wire format.
                            NetMessage packet = NetMessage.PlaceBand((int)turn, chosen.Id, move);
                            byte[] bytes = packet.Serialize();
                            totalBytes += bytes.Length;

                            if (!NetMessage.TryDeserialize(bytes, out NetMessage received))
                            {
                                mismatches++;
                                break;
                            }

                            Apply(host, host.Bands[chosen.Id], turn);

                            // The guest knows only the index, and must reach the same board from it.
                            if (received.A < 0 || received.A >= guest.Bands.Count ||
                                !guestRules.IsBandLegal(guest.Bands[received.A], out _))
                            {
                                mismatches++;
                                break;
                            }

                            Apply(guest, guest.Bands[received.A], (PlayerId)received.Seat);

                            totalMoves++;
                            turn = turn == PlayerId.Player1 ? PlayerId.Player2 : PlayerId.Player1;
                        }

                        if (!BoardsMatch(host, guest)) mismatches++;
                    }

                    if (mismatches > 0) failures++;

                    float bytesPerMove = totalMoves > 0 ? totalBytes / (float)totalMoves : 0f;

                    report.AppendLine(
                        $"    {radius,6} {GamesPerRadius,7} {totalMoves,15} " +
                        $"{(mismatches == 0 ? "yes" : $"NO ({mismatches})"),18} {bytesPerMove,12:0.0}");
                }
                finally
                {
                    if (hostGo != null) Object.DestroyImmediate(hostGo);
                    if (guestGo != null) Object.DestroyImmediate(guestGo);
                }
            }

            return failures;
        }

        /// <summary>Every piece of runtime state a divergence could hide in.</summary>
        private static bool BoardsMatch(BoardManager a, BoardManager b)
        {
            if (a.Cells.Count != b.Cells.Count || a.Edges.Count != b.Edges.Count ||
                a.Bands.Count != b.Bands.Count) return false;

            for (int i = 0; i < a.Cells.Count; i++)
                if (a.Cells[i].Owner != b.Cells[i].Owner) return false;

            for (int i = 0; i < a.Edges.Count; i++)
            {
                if (a.Edges[i].IsOccupied != b.Edges[i].IsOccupied) return false;
                if (a.Edges[i].BandCount != b.Edges[i].BandCount) return false;
                if (a.Edges[i].FirstCoveredBy != b.Edges[i].FirstCoveredBy) return false;
            }

            for (int i = 0; i < a.Bands.Count; i++)
            {
                if (a.Bands[i].IsPlaced != b.Bands[i].IsPlaced) return false;
                if (a.Bands[i].PlacedBy != b.Bands[i].PlacedBy) return false;
            }

            return true;
        }

        /// <summary>Mirrors GameFlowController's commit-and-claim step, minus the animation waits.</summary>
        private static void Apply(BoardManager board, BandPlacement band, PlayerId player)
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

            for (int i = 0; i < band.Edges.Length; i++)
            {
                List<TriangleCell> cells = band.Edges[i].Cells;

                for (int c = 0; c < cells.Count; c++)
                {
                    TriangleCell cell = cells[c];
                    if (cell == null || !seen.Add(cell)) continue;
                    if (cell.IsClaimed || !cell.IsFullyEnclosed) continue;

                    cell.Owner = player;
                }
            }
        }

        private static BoardManager NewBoard(int radius, out GameObject host)
        {
            host = new GameObject("~TriggleNetVerify") { hideFlags = HideFlags.HideAndDontSave };

            var board = host.AddComponent<BoardManager>();
            board.SetRadius(radius);
            board.Build();

            return board;
        }
    }
}

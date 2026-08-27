using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace Triggle.Net
{
    /// <summary>
    /// Creates and joins rooms: a Lobby for the six-character code and the player list, a Relay
    /// allocation for the bytes.
    /// </summary>
    /// <remarks>
    /// The two services do different jobs and neither replaces the other. Relay gives an allocation and
    /// its own join code, but that code is a long opaque string and Relay has no concept of who is in the
    /// room. Lobby gives a short code a player can read out, a roster, and a place to publish the Relay
    /// code to whoever joins - which is exactly how they are combined here: the Relay join code is stored
    /// as member-visible lobby data, so joining the lobby is what tells you where to connect.
    /// <para>
    /// Seats come from position in the lobby roster, so both devices derive the same running order from
    /// the same list rather than negotiating one.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class UgsRoomService : MonoBehaviour
    {
        /// <summary>Lobby data key holding the Relay join code.</summary>
        private const string RelayCodeKey = "relayJoinCode";

        /// <summary>
        /// Relay drops a lobby that goes quiet for 30 seconds, so the host pings well inside that.
        /// </summary>
        private const float HeartbeatSeconds = 15f;

        [Tooltip("Encrypted transport. Turn off only to debug with a packet capture.")]
        [SerializeField] private bool useDtls = true;

        [SerializeField] private bool verboseLogging;

        private Lobby _lobby;
        private Coroutine _heartbeat;

        /// <summary>The code another player types to join. Null when not in a room.</summary>
        public string RoomCode => _lobby?.LobbyCode;

        /// <summary>True while this device owns the room.</summary>
        public bool IsHost { get; private set; }

        /// <summary>Raised with a player-facing message when something goes wrong.</summary>
        public event Action<string> Failed;

        private string ConnectionType => useDtls ? "dtls" : "udp";

        // ------------------------------------------------------------------ sign-in

        /// <summary>
        /// Brings up Unity Services and signs in anonymously. Safe to call repeatedly.
        /// </summary>
        /// <remarks>
        /// Anonymous sign-in gives a stable per-device player id with nothing for the player to fill in,
        /// which is all the Lobby and Relay services need. Swapping it for real accounts later changes
        /// only this method.
        /// </remarks>
        public async Task<bool> EnsureSignedInAsync()
        {
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized)
                    await UnityServices.InitializeAsync();

                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();

                return true;
            }
            catch (Exception e)
            {
                Report($"Could not reach Unity Services: {e.Message}");
                return false;
            }
        }

        // ------------------------------------------------------------------ hosting

        /// <summary>
        /// Allocates a Relay endpoint, publishes its code in a new lobby, and returns a connected
        /// transport. Null on failure, with <see cref="Failed"/> already raised.
        /// </summary>
        public async Task<UgsSessionTransport> HostAsync(int maxPlayers, string playerName)
        {
            if (!await EnsureSignedInAsync()) return null;

            maxPlayers = Mathf.Clamp(maxPlayers, 2, 4);

            try
            {
                // maxPlayers - 1: the allocation counts connections to the host, not seats at the table.
                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1);
                string relayCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

                var options = new CreateLobbyOptions
                {
                    IsPrivate = false,
                    Player = BuildPlayer(playerName),
                    Data = new Dictionary<string, DataObject>
                    {
                        // Member-visible, so only someone who has joined can read where to connect.
                        [RelayCodeKey] = new DataObject(DataObject.VisibilityOptions.Member, relayCode)
                    }
                };

                _lobby = await LobbyService.Instance.CreateLobbyAsync("Triggle", maxPlayers, options);
                IsHost = true;

                _heartbeat = StartCoroutine(HeartbeatRoutine());

                Log($"hosting room {_lobby.LobbyCode} (relay {relayCode})");

                var relayData = new RelayServerData(allocation, ConnectionType);
                return new UgsSessionTransport(relayData, true, 1, _lobby.LobbyCode, verboseLogging);
            }
            catch (Exception e)
            {
                Report($"Could not create the room: {e.Message}");
                await LeaveAsync();
                return null;
            }
        }

        // ------------------------------------------------------------------ joining

        /// <summary>
        /// Joins a room by its code and returns a connected transport. Null on failure.
        /// </summary>
        public async Task<UgsSessionTransport> JoinAsync(string roomCode, string playerName)
        {
            if (string.IsNullOrWhiteSpace(roomCode))
            {
                Report("Enter a room code first.");
                return null;
            }

            if (!await EnsureSignedInAsync()) return null;

            try
            {
                var options = new JoinLobbyByCodeOptions { Player = BuildPlayer(playerName) };

                _lobby = await LobbyService.Instance.JoinLobbyByCodeAsync(
                    roomCode.Trim().ToUpperInvariant(), options);

                IsHost = false;

                if (!_lobby.Data.TryGetValue(RelayCodeKey, out DataObject relayCode) ||
                    string.IsNullOrEmpty(relayCode.Value))
                {
                    Report("That room is not ready yet - ask the host to try again.");
                    await LeaveAsync();
                    return null;
                }

                JoinAllocation allocation =
                    await RelayService.Instance.JoinAllocationAsync(relayCode.Value);

                int seat = SeatOf(_lobby);
                Log($"joined room {_lobby.LobbyCode} as seat {seat}");

                var relayData = new RelayServerData(allocation, ConnectionType);
                return new UgsSessionTransport(relayData, false, seat, _lobby.LobbyCode, verboseLogging);
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyNotFound)
            {
                Report("No room with that code.");
                return null;
            }
            catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyFull)
            {
                Report("That room is full.");
                return null;
            }
            catch (Exception e)
            {
                Report($"Could not join: {e.Message}");
                await LeaveAsync();
                return null;
            }
        }

        // ------------------------------------------------------------------ leaving

        /// <summary>Leaves or deletes the room. Safe to call when not in one.</summary>
        public async Task LeaveAsync()
        {
            if (_heartbeat != null)
            {
                StopCoroutine(_heartbeat);
                _heartbeat = null;
            }

            Lobby lobby = _lobby;
            _lobby = null;

            if (lobby == null) return;

            try
            {
                // Deleting rather than leaving when hosting: the room is useless without the host's
                // Relay allocation, and an abandoned lobby would sit in the list until it timed out.
                if (IsHost) await LobbyService.Instance.DeleteLobbyAsync(lobby.Id);
                else await LobbyService.Instance.RemovePlayerAsync(lobby.Id, AuthenticationService.Instance.PlayerId);
            }
            catch (Exception e)
            {
                // Already gone, or the network went away with it. Nothing useful left to do.
                Log($"leaving the room reported: {e.Message}");
            }
            finally
            {
                IsHost = false;
            }
        }

        private void OnDestroy()
        {
            if (_heartbeat != null) StopCoroutine(_heartbeat);

            // Fire and forget: the object is going away and cannot await anything.
            if (_lobby != null) _ = LeaveAsync();
        }

        // ------------------------------------------------------------------ internals

        private IEnumerator HeartbeatRoutine()
        {
            var wait = new WaitForSecondsRealtime(HeartbeatSeconds);

            while (_lobby != null)
            {
                yield return wait;

                if (_lobby == null) yield break;

                _ = SendHeartbeatAsync(_lobby.Id);
            }
        }

        private async Task SendHeartbeatAsync(string lobbyId)
        {
            try
            {
                await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
            }
            catch (Exception e)
            {
                Log($"heartbeat failed: {e.Message}");
            }
        }

        /// <summary>
        /// Seat number from position in the roster, so every device derives the same running order from
        /// the same list instead of negotiating one.
        /// </summary>
        private static int SeatOf(Lobby lobby)
        {
            string me = AuthenticationService.Instance.PlayerId;

            for (int i = 0; i < lobby.Players.Count; i++)
                if (lobby.Players[i].Id == me) return i + 1;

            return lobby.Players.Count + 1;
        }

        private static Unity.Services.Lobbies.Models.Player BuildPlayer(string playerName)
        {
            return new Unity.Services.Lobbies.Models.Player(
                id: AuthenticationService.Instance.PlayerId,
                data: new Dictionary<string, PlayerDataObject>
                {
                    ["name"] = new PlayerDataObject(
                        PlayerDataObject.VisibilityOptions.Member,
                        string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName)
                });
        }

        private void Report(string message)
        {
            Debug.LogWarning($"[Triggle] Room: {message}", this);
            Failed?.Invoke(message);
        }

        private void Log(string message)
        {
            if (verboseLogging) Debug.Log($"[Triggle] Room: {message}", this);
        }
    }
}

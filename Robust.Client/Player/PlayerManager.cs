﻿using System;
using System.Collections.Generic;
using System.Linq;
using Robust.Client.Interfaces;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Interfaces.Configuration;
using Robust.Shared.Interfaces.GameObjects;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.IoC;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Utility;
using Robust.Shared.ViewVariables;

namespace Robust.Client.Player
{
    /// <summary>
    ///     Here's the player controller. This will handle attaching GUIs and input to controllable things.
    ///     Why not just attach the inputs directly? It's messy! This makes the whole thing nicely encapsulated.
    ///     This class also communicates with the server to let the server control what entity it is attached to.
    /// </summary>
    public class PlayerManager : IPlayerManager
    {
        [Dependency]
#pragma warning disable 649
        private readonly IClientNetManager _network;

        [Dependency]
        private readonly IBaseClient _client;

        [Dependency]
        private readonly IConfigurationManager _config;

        [Dependency]
        private readonly IEntityManager _entityManager;
#pragma warning restore 649

        /// <summary>
        ///     Active sessions of connected clients to the server.
        /// </summary>
        private Dictionary<NetSessionId, IPlayerSession> _sessions;

        /// <inheritdoc />
        public int PlayerCount => _sessions.Values.Count;

        /// <inheritdoc />
        public int MaxPlayers => _client.GameInfo.ServerMaxPlayers;

        /// <inheritdoc />
        [ViewVariables] public LocalPlayer LocalPlayer { get; private set; }

        /// <inheritdoc />
        [ViewVariables] public IEnumerable<IPlayerSession> Sessions => _sessions.Values;

        /// <inheritdoc />
        public IReadOnlyDictionary<NetSessionId, IPlayerSession> SessionsDict => _sessions;

        /// <inheritdoc />
        public event EventHandler PlayerListUpdated;

        /// <inheritdoc />
        public void Initialize()
        {
            _sessions = new Dictionary<NetSessionId, IPlayerSession>();

            _config.RegisterCVar("player.name", "JoeGenero", CVar.ARCHIVE);

            _network.RegisterNetMessage<MsgPlayerListReq>(MsgPlayerListReq.NAME);
            _network.RegisterNetMessage<MsgPlayerList>(MsgPlayerList.NAME, HandlePlayerList);
        }

        /// <inheritdoc />
        public void Startup(INetChannel channel)
        {
            LocalPlayer = new LocalPlayer(_network, _config);

            var msgList = _network.CreateNetMessage<MsgPlayerListReq>();
            // message is empty
            _network.ClientSendMessage(msgList);
        }

        /// <inheritdoc />
        public void Update(float frameTime)
        {
            // Uh, nothing anymore I guess.
        }

        /// <inheritdoc />
        public void Shutdown()
        {
            LocalPlayer = null;
            _sessions.Clear();
        }

        /// <inheritdoc />
        public void ApplyPlayerStates(IEnumerable<PlayerState> list)
        {
            if (list == null)
            {
                // This happens when the server says "nothing changed!"
                return;
            }
            DebugTools.Assert(_network.IsConnected, "Received player state without being connected?");
            DebugTools.Assert(LocalPlayer != null, "Call Startup()");
            DebugTools.Assert(LocalPlayer.Session != null, "Received player state before Session finished setup.");

            var myState = list.FirstOrDefault(s => s.SessionId == LocalPlayer.SessionId);

            if (myState != null)
            {
                UpdateAttachedEntity(myState.ControlledEntity);
                UpdateSessionStatus(myState.Status);
            }

            UpdatePlayerList(list);
        }

        /// <summary>
        ///     Compares the server sessionStatus to the client one, and updates if needed.
        /// </summary>
        private void UpdateSessionStatus(SessionStatus myStateStatus)
        {
            if (LocalPlayer.Session.Status != myStateStatus)
                LocalPlayer.SwitchState(myStateStatus);
        }

        /// <summary>
        ///     Compares the server attachedEntity to the client one, and updates if needed.
        /// </summary>
        /// <param name="entity">AttachedEntity in the server session.</param>
        private void UpdateAttachedEntity(EntityUid? entity)
        {
            if (LocalPlayer.ControlledEntity?.Uid == entity)
            {
                return;
            }

            if (entity == null)
            {
                LocalPlayer.DetachEntity();
                return;
            }

            LocalPlayer.AttachEntity(_entityManager.GetEntity(entity.Value));
        }

        /// <summary>
        ///     Handles the incoming PlayerList message from the server.
        /// </summary>
        private void HandlePlayerList(MsgPlayerList msg)
        {
            UpdatePlayerList(msg.Plyrs);
        }

        /// <summary>
        ///     Compares the server player list to the client one, and updates if needed.
        /// </summary>
        private void UpdatePlayerList(IEnumerable<PlayerState> remotePlayers)
        {
            var dirty = false;

            var hitSet = new List<NetSessionId>();

            foreach (var state in remotePlayers)
            {
                hitSet.Add(state.SessionId);

                if (_sessions.TryGetValue(state.SessionId, out var local))
                {
                    // Exists, update data.
                    if (local.Name == state.Name && local.Status == state.Status && local.Ping == state.Ping)
                        continue;

                    dirty = true;
                    local.Name = state.Name;
                    local.Status = state.Status;
                    local.Ping = state.Ping;
                }
                else
                {
                    // New, give him a slot.
                    dirty = true;

                    var newSession = new PlayerSession(state.SessionId)
                    {
                        Name = state.Name,
                        Status = state.Status,
                        Ping = state.Ping
                    };
                    _sessions.Add(state.SessionId, newSession);
                    if (state.SessionId == LocalPlayer.SessionId)
                    {
                        LocalPlayer.Session = newSession;

                        // We just connected to the server, hurray!
                        LocalPlayer.SwitchState(SessionStatus.Connecting, newSession.Status);
                    }
                }
            }

            foreach (var existing in _sessions.Keys.ToArray())
            {
                // clear slot, player left
                if (!hitSet.Contains(existing))
                {
                    DebugTools.Assert(LocalPlayer.SessionId != existing, "I'm still connected to the server, but i left?");
                    _sessions.Remove(existing);
                    dirty = true;
                }
            }

            if (dirty)
            {
                PlayerListUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

using HarmonyLib;
using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace oomtm450PuckMod_SkatersLimit {
    public class SkatersLimit : IPuckMod {
        #region Constants
        private const string GOALIE_POSITION = "G";
        private const string MOD_VERSION = "1.0.3";
        #endregion

        #region Fields
        private static readonly Harmony _harmony = new Harmony(Constants.MOD_NAME);
        private static ServerConfig _serverConfig = new ServerConfig();
        private static ClientConfig _clientConfig = new ClientConfig();
        private static string _serverVersion = "";
        #endregion

        [HarmonyPatch(typeof(PlayerPositionManagerController), "Event_Client_OnPositionSelectClickPosition")]
        public class PlayerPositionManagerControllerPatch {
            [HarmonyPrefix]
            public static bool Prefix(Dictionary<string, object> message) {
                // If this is the server or the config was not sent by server (not installed on the server ?), do not use the patch.
                if (IsDedicatedServer() || !_serverConfig.SentByServer)
                    return true;

                Log("Event_Client_OnPositionSelectClickPosition");

                PlayerPosition currentPPosition = (PlayerPosition)message["playerPosition"];

                if (currentPPosition.Name == GOALIE_POSITION)
                    return true;

                // Admin bypass.
                if (_serverConfig.AdminBypass) {
                    Player currentPlayer = PlayerManager.Instance.GetLocalPlayer();
                    foreach (string adminSteamId in _serverConfig.AdminSteamIds) {
                        if (adminSteamId == currentPlayer.SteamId.Value.ToString()) {
                            Log($"{adminSteamId} is an admin. Bypassing team limits.");
                            return true;
                        }
                    }
                }

                bool hasBlueGoalie = false;
                int numberOfBlueSkaters = 0;
                foreach (PlayerPosition pPosition in PlayerPositionManager.Instance.BluePositions) {
                    if (IsAttacker(pPosition))
                        numberOfBlueSkaters++;
                    if (IsGoalie(pPosition))
                        hasBlueGoalie = true;
                }

                bool hasRedGoalie = false;
                int numberOfRedSkaters = 0;
                foreach (PlayerPosition pPosition in PlayerPositionManager.Instance.RedPositions) {
                    if (IsAttacker(pPosition))
                        numberOfRedSkaters++;
                    if (IsGoalie(pPosition))
                        hasRedGoalie = true;
                }

                int maxNumberOfSkaters = _serverConfig.MaxNumberOfSkaters;
                bool teamBalancing = TeamBalancing(hasBlueGoalie, hasRedGoalie);

                string team;
                int numberOfSkaters;
                bool goalieAvailable = true;
                switch (currentPPosition.Team) {
                    case PlayerTeam.Blue:
                        team = "blue";
                        numberOfSkaters = numberOfBlueSkaters;

                        if (teamBalancing) {
                            int newMaxNumberOfSkaters = numberOfRedSkaters + _serverConfig.TeamBalanceOffset + 1;
                            if (newMaxNumberOfSkaters < maxNumberOfSkaters)
                                maxNumberOfSkaters = newMaxNumberOfSkaters;
                        }

                        if (hasBlueGoalie)
                            goalieAvailable = false;

                        break;

                    case PlayerTeam.Red:
                        team = "red";
                        numberOfSkaters = numberOfRedSkaters;

                        if (teamBalancing) {
                            int newMaxNumberOfSkaters = numberOfBlueSkaters + _serverConfig.TeamBalanceOffset + 1;
                            if (newMaxNumberOfSkaters < maxNumberOfSkaters)
                                maxNumberOfSkaters = newMaxNumberOfSkaters;
                        }

                        if (hasRedGoalie)
                            goalieAvailable = false;

                        break;

                    default:
                        LogError("No team assigned to the current player position ?");
                        return true;
                }

                if (teamBalancing)
                    Log("Team balancing is on.");

                Log($"Current team : {team} with {numberOfSkaters} skaters.");
                Log($"Current number of skaters on red team : {numberOfRedSkaters}.");
                Log($"Current number of skaters on blue team : {numberOfBlueSkaters}.");

                if (numberOfSkaters >= maxNumberOfSkaters) {
                    if (teamBalancing) {
                        if (goalieAvailable)
                            UIChat.Instance.AddChatMessage($"Teams are unbalanced ({maxNumberOfSkaters}). Go goalie or switch teams.");
                        else
                            UIChat.Instance.AddChatMessage($"Teams are unbalanced ({maxNumberOfSkaters}). Switch teams.");
                    }
                    else {
                        if (goalieAvailable)
                            UIChat.Instance.AddChatMessage($"Team is full ({maxNumberOfSkaters}). Only {GOALIE_POSITION} position is available.");
                        else
                            UIChat.Instance.AddChatMessage($"Team is full ({maxNumberOfSkaters}). Switch teams.");
                    }
                        
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Function that returns true if the PlayerPosition has the given role, and if it is claimed depending on hasToBeClaimed.
        /// </summary>
        /// <param name="pPosition">PlayerPosition, object to check.</param>
        /// <param name="role">PlayerRole, role to check for in the PlayerPosition.</param>
        /// <param name="hasToBeClaimed">Bool, true if the PlayerPosition has to be claimed.</param>
        /// <returns>Bool, true if the PlayerPosition has the given PlayerRole, and is claimed or not depending of hasToBeClaimed.</returns>
        private static bool IsRole(PlayerPosition pPosition, PlayerRole role, bool hasToBeClaimed = true) {
            bool output = pPosition.Role == role;
            if (hasToBeClaimed)
                return output && pPosition.IsClaimed;

            return output;
        }

        /// <summary>
        /// Function that returns true if the PlayerPosition is an attacker (skater), and if it is claimed depending on hasToBeClaimed.
        /// </summary>
        /// <param name="pPosition">PlayerPosition, object to check.</param>
        /// <param name="hasToBeClaimed">Bool, true if the PlayerPosition has to be claimed.</param>
        /// <returns>Bool, true if the PlayerPosition is an attacker (skater), and is claimed or not depending of hasToBeClaimed.</returns>
        private static bool IsAttacker(PlayerPosition pPosition, bool hasToBeClaimed = true) {
            return IsRole(pPosition, PlayerRole.Attacker, hasToBeClaimed);
        }

        /// <summary>
        /// Function that returns true if the PlayerPosition is a goalie, and if it is claimed depending on hasToBeClaimed.
        /// </summary>
        /// <param name="pPosition">PlayerPosition, object to check.</param>
        /// <param name="hasToBeClaimed">Bool, true if the PlayerPosition has to be claimed.</param>
        /// <returns>Bool, true if the PlayerPosition is a goalie, and is claimed or not depending of hasToBeClaimed.</returns>
        private static bool IsGoalie(PlayerPosition pPosition, bool hasToBeClaimed = true) {
            return IsRole(pPosition, PlayerRole.Goalie, hasToBeClaimed);
        }

        public static void Event_Client_OnClientStarted(Dictionary<string, object> message) {
            if (NetworkManager.Singleton == null || IsDedicatedServer())
                return;

            Log("Event_Client_OnClientStarted");

            try {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(Constants.FROM_SERVER, ReceiveData);
            }
            catch (Exception ex) {
                LogError($"Error in Event_Client_OnClientStarted. {ex}");
            }
        }

        public static void Event_Client_OnClientStopped(Dictionary<string, object> message) {
            Log("Event_Client_OnClientStopped");

            try {
                _serverConfig = new ServerConfig();
            }
            catch (Exception ex) {
                LogError($"Error in Event_Client_OnClientStopped. {ex}");
            }
        }

        public static void Event_OnPlayerSpawned(Dictionary<string, object> message) {
            if (!IsDedicatedServer())
                return;
            
            Log("Event_OnPlayerSpawned");

            try {
                Player player = (Player)message["player"];

                NetworkCommunication.SendData(nameof(MOD_VERSION), MOD_VERSION, player.OwnerClientId, Constants.FROM_SERVER);
                NetworkCommunication.SendData("config", _serverConfig.ToString(), player.OwnerClientId, Constants.FROM_SERVER);
            }
            catch (Exception ex) {
                LogError($"Error in Event_OnPlayerSpawned. {ex}");
            }
        }

        public static void ReceiveData(ulong clientId, FastBufferReader reader) {
            Log("ReceiveData");
            
            try {
                (string dataName, string dataStr) = NetworkCommunication.GetData(clientId, reader);

                switch (dataName) {
                    case nameof(MOD_VERSION):
                        _serverVersion = dataStr;
                        if (MOD_VERSION != _serverVersion) // TODO : Move the kick later so that it doesn't break anything. Maybe even add a chat message and a 3-5 sec wait.
                            NetworkCommunication.SendData("kick", "1", clientId, Constants.FROM_SERVER);
                        break;

                    case "config":
                        _serverConfig = ServerConfig.SetConfig(dataStr);
                        break;

                    case "kick":
                        if (dataStr == "1") {
                            Log($"Kicking client {clientId}.");
                            NetworkManager.Singleton.DisconnectClient(clientId, "Mod is out of date, please restart your game.");
                        }
                        break;
                }
            }
            catch (Exception ex) {
                LogError($"Error in ReceiveData: {ex}");
            }
        }

        /// <summary>
        /// Method that launches when the mod is being enabled.
        /// </summary>
        /// <returns>Bool, true if the mod successfully enabled.</returns>
        public bool OnEnable()  {
            try
            {
                Log($"Enabling...", true);

                _harmony.PatchAll();

                Log($"Enabled.", true);

                if (IsDedicatedServer()) {
                    Log("Setting server sided config.", true);
                    NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(Constants.FROM_CLIENT, ReceiveData);

                    _serverConfig = ServerConfig.ReadConfig(ServerManager.Instance.AdminSteamIds);
                }
                else {
                    Log("Setting client sided config.", true);
                    _clientConfig = ClientConfig.ReadConfig();
                }

                Log("Subscribing to events.", true);
                EventManager.Instance.AddEventListener("Event_Client_OnClientStarted", Event_Client_OnClientStarted);
                EventManager.Instance.AddEventListener("Event_Client_OnClientStopped", Event_Client_OnClientStopped);
                EventManager.Instance.AddEventListener("Event_OnPlayerSpawned", Event_OnPlayerSpawned);

                return true;
            }
            catch (Exception ex) {
                LogError($"Failed to enable: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Method that launches when the mod is being disabled.
        /// </summary>
        /// <returns>Bool, true if the mod successfully disabled.</returns>
        public bool OnDisable() {
            try {
                Log("Unsubscribing from events.", true);

                EventManager.Instance.RemoveEventListener("Event_Client_OnClientStarted", Event_Client_OnClientStarted);
                EventManager.Instance.RemoveEventListener("Event_Client_OnClientStopped", Event_Client_OnClientStopped);
                EventManager.Instance.RemoveEventListener("Event_OnPlayerSpawned", Event_OnPlayerSpawned);

                Log($"Disabling...", true);

                _harmony.UnpatchSelf();

                Log($"Disabled.", true);
                return true;
            }
            catch (Exception ex) {
                LogError($"Failed to disable: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Function that logs information to the debug console.
        /// </summary>
        /// <param name="msg">String, message to log.</param>
        /// <param name="bypassConfig">Bool, true to bypass the logs config. False by default.</param>
        internal static void Log(string msg, bool bypassConfig = false) {
            if (bypassConfig || (IsDedicatedServer() && _serverConfig.LogInfo) || (!IsDedicatedServer() && _clientConfig.LogInfo))
                Debug.Log($"[{Constants.MOD_NAME}] {msg}");
        }

        /// <summary>
        /// Function that logs errors to the debug console.
        /// </summary>
        /// <param name="msg">String, message to log.</param>
        internal static void LogError(string msg) {
            Debug.LogError($"[{Constants.MOD_NAME}] {msg}");
        }

        /// <summary>
        /// Function that returns true if the instance is a dedicated server.
        /// </summary>
        /// <returns>Bool, true if this is a dedicated server.</returns>
        private static bool IsDedicatedServer() {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        }

        /// <summary>
        /// Function that returns true if team balancing is activated.
        /// </summary>
        /// <param name="hasBlueGoalie">Bool, true if blue team has a goalie.</param>
        /// <param name="hasRedGoalie">Bool, true if red team has a goalie.</param>
        /// <returns>Bool, true if team balancing is activated.</returns>
        private static bool TeamBalancing(bool hasBlueGoalie, bool hasRedGoalie) {
            if (_serverConfig.TeamBalancing)
                return true;

            if (!_serverConfig.TeamBalancingGoalie)
                return false;

            if (hasBlueGoalie && hasRedGoalie)
                return false;

            if (hasBlueGoalie || hasRedGoalie)
                return true;

            return false;
        }
    }
}

using HarmonyLib;
using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace oomtm450PuckMod_SkatersLimit {
    public class SkatersLimit : IPuckMod {
        #region Constants
        private const string GOALIE_POSITION = "G";
        private const string MOD_VERSION = "1.0.0DEV";
        #endregion

        #region Fields
        private static readonly Harmony _harmony = new Harmony(Constants.MOD_NAME);
        private static ServerConfig _serverConfig = new ServerConfig();
        private static ClientConfig _clientConfig = new ClientConfig(); // TODO : Read local client config.
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
                    Player currentPlayer = NetworkBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(currentPPosition.OwnerClientId);
                    foreach (string adminSteamId in NetworkBehaviourSingleton<ServerManager>.Instance.AdminSteamIds) {
                        if (adminSteamId == currentPlayer.SteamId.Value.ToString())
                            return true;
                    }
                }

                bool hasBlueGoalie = false;
                int numberOfBlueSkaters = 0;
                foreach (PlayerPosition pPosition in PlayerPositionManager.Instance.BluePositions) {
                    if (pPosition.Role == PlayerRole.Attacker && pPosition.IsClaimed)
                        numberOfBlueSkaters++;
                    if (pPosition.Role == PlayerRole.Goalie && pPosition.IsClaimed)
                        hasBlueGoalie = true;
                }

                bool hasRedGoalie = false;
                int numberOfRedSkaters = 0;
                foreach (PlayerPosition pPosition in PlayerPositionManager.Instance.RedPositions) {
                    if (pPosition.Role == PlayerRole.Attacker && pPosition.IsClaimed)
                        numberOfRedSkaters++;
                    if (pPosition.Role == PlayerRole.Goalie && pPosition.IsClaimed)
                        hasRedGoalie = true;
                }

                int maxNumberOfSkaters = _serverConfig.MaxNumberOfSkaters;
                bool teamBalancing = TeamBalancing(hasBlueGoalie, hasRedGoalie);

                string team;
                int numberOfSkaters;
                switch (currentPPosition.Team) {
                    case PlayerTeam.Blue:
                        team = "blue";
                        numberOfSkaters = numberOfBlueSkaters;

                        if (teamBalancing)
                            maxNumberOfSkaters = numberOfRedSkaters + _serverConfig.TeamBalanceOffset + 1;

                        break;

                    case PlayerTeam.Red:
                        team = "red";
                        numberOfSkaters = numberOfRedSkaters;

                        if (teamBalancing)
                            maxNumberOfSkaters = numberOfBlueSkaters + _serverConfig.TeamBalanceOffset + 1;

                        break;

                    default:
                        LogError("No team assigned to the current player position ?");
                        return true;
                }

                Log($"Current team : {team} with {numberOfSkaters} skaters.");
                Log($"Current number of skaters on red team : {numberOfRedSkaters}.");
                Log($"Current number of skaters on blue team : {numberOfBlueSkaters}.");

                if (numberOfSkaters >= maxNumberOfSkaters) {
                    if (teamBalancing)
                        UIChat.Instance.AddChatMessage($"Teams are unbalanced ({maxNumberOfSkaters}). Go goalie or switch team.");
                    else
                        UIChat.Instance.AddChatMessage($"Team is full ({maxNumberOfSkaters}). Only {GOALIE_POSITION} position is available.");
                    return false;
                }

                return true;
            }
        }

        public static void Event_Client_OnClientStarted(Dictionary<string, object> message) {
            if (NetworkManager.Singleton == null || IsDedicatedServer())
                return;

            Log("Event_Client_OnClientStarted");

            try {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(Constants.SERVER_ID, ReceiveData);
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

                NetworkCommunication.SendData(nameof(MOD_VERSION), MOD_VERSION, player.OwnerClientId, Constants.SERVER_ID);
                NetworkCommunication.SendData("config", _serverConfig.ToString(), player.OwnerClientId, Constants.SERVER_ID);
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
                        if (MOD_VERSION != _serverVersion) // TODO : Move the kick later so that it doesn't bug anything. Maybe even add a chat message and a 3-5 sec wait.
                            NetworkCommunication.SendData("kick", "1", clientId, Constants.CLIENT_ID);
                        break;

                    case "config":
                        _serverConfig = ServerConfig.SetConfig(dataStr);
                        break;
                }
            }
            catch (Exception ex) {
                LogError($"Error in ReceiveData: {ex}");
            }
        }

        public static void KickClient(ulong clientId, FastBufferReader reader) {
            Log("KickClient");

            try {
                if (NetworkCommunication.GetData(clientId, reader).DataStr == "1") {
                    Log($"Kicking client {clientId}.");
                    NetworkManager.Singleton.DisconnectClient(clientId, "Mod is out of date, please restart your game.");
                }  
            }
            catch (Exception ex) {
                LogError($"Error in KickClient: {ex}");
            }
        }

        /// <summary>
        /// Method that launches when the mod is being enabled.
        /// </summary>
        /// <returns>Bool, true if the mod successfully enabled.</returns>
        public bool OnEnable()  {
            try
            {
                Log($"Enabling...");

                _harmony.PatchAll();

                Log($"Enabled.");

                if (IsDedicatedServer()) {
                    Log("Setting server sided config.");
                    NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(Constants.CLIENT_ID, KickClient);

                    _serverConfig = ServerConfig.ReadConfig();
                }

                Log("Subscribing to events.");
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
                Log("Unsubscribing from events.");

                EventManager.Instance.RemoveEventListener("Event_Client_OnClientStarted", Event_Client_OnClientStarted);
                EventManager.Instance.RemoveEventListener("Event_Client_OnClientStopped", Event_Client_OnClientStopped);
                EventManager.Instance.RemoveEventListener("Event_OnPlayerSpawned", Event_OnPlayerSpawned);

                Log($"Disabling...");

                _harmony.UnpatchSelf();

                Log($"Disabled.");
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
        internal static void Log(string msg) {
            if ((IsDedicatedServer() && _serverConfig.LogInfo) || (!IsDedicatedServer() && _clientConfig.LogInfo))
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

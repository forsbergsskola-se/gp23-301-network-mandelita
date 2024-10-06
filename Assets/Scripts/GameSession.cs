using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameSession : MonoBehaviour
{
    private const int UDPPortNumber = 50000;
    private const int TcpPortNumber = 44446;
    private bool finishedLoading;
    private PlayerController playerController;
    public bool isServer;

    #region ------ Client -------
    private IPEndPoint serverEndpointUDP;
    private IPEndPoint serverEndpointTCP;
    private UdpClient udpClient;
    private TcpClient tcpClient;
    #endregion

    #region ------ Server -------
    private Dictionary<IPEndPoint, OpponentController> opponents = new();
    private List<IPEndPoint> clients = new();
    private TcpListener tcpListener;
    private bool udpReady;
    #endregion

    private async void FixedUpdate()
    {
        if (!finishedLoading || !udpReady) return;

        if (!isServer)
        {
            await SendPositionToServer();
            await ReceiveOpponentUpdates();
        }
        else
        {
            await ReceivePositions();
        }
    }

    private async Task SendPositionToServer()
    {
        if (!udpReady) return;

        var position = playerController.transform.position;
        var size = playerController.GetComponent<Blob>().Size;

        var state = new PlayerState(position, size);
        var stateJson = JsonUtility.ToJson(state);
        var bytes = Encoding.UTF8.GetBytes(stateJson);

        Debug.Log("Client sending position update...");

        try
        {
            await udpClient.SendAsync(bytes, bytes.Length, serverEndpointUDP);
            Debug.Log("Client sent position!");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error sending UDP packet: {ex.Message}");
        }
    }

    private async Task ReceivePositions()
    {
        Debug.Log("Server listening for positions...");

        while (true)
        {
            try
            {
                var receiveResult = await udpClient.ReceiveAsync();
                var fromEndpoint = receiveResult.RemoteEndPoint;

                // Skip processing if it's from the server's own UDP endpoint
                if (fromEndpoint.Equals(serverEndpointUDP)) 
                    continue;

                var receivedJson = Encoding.UTF8.GetString(receiveResult.Buffer);
                Debug.Log($"Received position from {fromEndpoint}");

                if (!IsValidJson(receivedJson))
                {
                    Debug.LogWarning("Invalid JSON format received, skipping packet.");
                    continue;
                }

                try
                {
                    var playerState = JsonUtility.FromJson<PlayerState>(receivedJson);
                    if (playerState == null)
                    {
                        Debug.LogWarning("Parsed player state is null, skipping update.");
                        continue;
                    }

                    // Update existing opponent without spawning new ones
                    if (opponents.TryGetValue(fromEndpoint, out var opponentController) && opponentController != null)
                    {
                        opponentController.UpdatePosition(playerState.position, playerState.size);
                        BroadcastOpponentStates();
                    }
                    else
                    {
                        Debug.LogWarning($"No opponent found for endpoint {fromEndpoint}; skipping update.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error parsing JSON: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error receiving UDP packets: {ex.Message}");
            }

            await Task.Yield();
        }
    }



// Helper function to validate JSON format. Found this to fix all errors in ReceivePositions
    private bool IsValidJson(string json)
    {
        json = json.Trim();
        if ((json.StartsWith("{") && json.EndsWith("}")) || (json.StartsWith("[") && json.EndsWith("]")))
        {
            try
            {
                var obj = JsonUtility.FromJson<PlayerState>(json);
                return obj != null;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private async Task ReceiveOpponentUpdates()
    {
        while (true)
        {
            try
            {
                var receiveResult = await udpClient.ReceiveAsync();
                var receivedJson = Encoding.UTF8.GetString(receiveResult.Buffer);
            
                // Validate and parse JSON data
                if (!IsValidJson(receivedJson))
                {
                    Debug.LogWarning("Invalid JSON received for opponent update.");
                    continue;
                }
            
                var opponentState = JsonUtility.FromJson<PlayerState>(receivedJson);
                Debug.Log("Client received opponent update");

                // Ensure an opponent is spawned or updated
                var opponentEndpoint = receiveResult.RemoteEndPoint;
                if (!opponents.ContainsKey(opponentEndpoint))
                {
                    Debug.Log($"Spawning new opponent on client for {opponentEndpoint}");
                    opponents[opponentEndpoint] = SpawnOpponent();
                }

                // Update the opponent's position and size
                opponents[opponentEndpoint]?.UpdatePosition(opponentState.position, opponentState.size);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error receiving opponent update: {ex.Message}");
            }

            await Task.Yield(); // Yield to prevent blocking
        }
    }


    private void BroadcastOpponentStates()
    {
        foreach (var opponent in opponents)
        {
            if (opponent.Value == null) continue; // Ensure the opponent is still valid

            var state = new PlayerState(opponent.Value.transform.position, opponent.Value.GetComponent<Blob>().Size);
            var stateJson = JsonUtility.ToJson(state);
            var bytes = Encoding.UTF8.GetBytes(stateJson);

            Debug.Log("Broadcasting opponent state to all clients");

            foreach (var client in clients)
            {
                if (client != null && !client.Equals(serverEndpointUDP)) // Avoid sending to server itself
                {
                    udpClient.SendAsync(bytes, bytes.Length, client);
                    Debug.Log($"Sent opponent state to {client}");
                }
            }
        }
    }


    private void EnsureOpponentAndUpdatePosition(IPEndPoint opponentEndpoint, Vector3 position, float size)
    {
        if (!opponents.TryGetValue(opponentEndpoint, out var opponentController))
        {
            Debug.Log($"Spawning new opponent for {opponentEndpoint}");

            // Avoid spawning for the host's own endpoint
            if (opponentEndpoint.Equals(serverEndpointUDP))
            {
                Debug.Log("Skipping spawn for host’s own endpoint.");
                return;
            }

            opponentController = SpawnOpponent();
            opponents[opponentEndpoint] = opponentController;
        }

        // Check if the opponentController or its GameObject is destroyed before updating
        if (opponentController != null && opponentController.gameObject != null)
        {
            Debug.Log($"Updating position for {opponentEndpoint}");
            opponentController.UpdatePosition(position, size);
        }
        else
        {
            Debug.LogWarning($"Opponent for {opponentEndpoint} has been destroyed or is null.");
            opponents.Remove(opponentEndpoint); // Remove invalid or destroyed references
        }
    }



    public static void HostGame()
    {
        try
        {
            var session = CreateNew();
            session.isServer = true;

            session.udpClient = new UdpClient(UDPPortNumber);
            Debug.Log("UDP Listener started on port " + UDPPortNumber);

            session.tcpListener = new TcpListener(IPAddress.Any, TcpPortNumber);
            session.tcpListener.Start();
            Debug.Log("TCP Listener started on port " + TcpPortNumber);

            session.StartCoroutine(session.Co_AcceptClients());
            session.StartCoroutine(session.Co_LaunchGame());

            Debug.Log("HostGame successfully started");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error in HostGame: " + ex.Message);
        }
    }

    private IEnumerator Co_AcceptClients()
    {
        Debug.Log("Waiting for TCP clients to connect...");

        while (true)
        {
            var task = tcpListener.AcceptTcpClientAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            var client = task.Result;
            var clientEndpoint = (IPEndPoint)client.Client.RemoteEndPoint;

            Debug.Log($"Client connected via TCP from {clientEndpoint}");

            if (!clients.Contains(clientEndpoint))
            {
                clients.Add(clientEndpoint);

                // Only spawn if opponent does not already exist
                if (!opponents.ContainsKey(clientEndpoint))
                {
                    Debug.Log($"Spawning new opponent for client {clientEndpoint}");
                    var newOpponent = SpawnOpponent();
                    opponents[clientEndpoint] = newOpponent;
                }
                else
                {
                    Debug.LogWarning($"Opponent already exists for {clientEndpoint}, skipping spawn.");
                }

                // Send host state to the newly connected client
                SendHostStateToClient(clientEndpoint);
                Debug.Log($"Opponent spawned and host state sent for {clientEndpoint}");
            }
            else
            {
                Debug.LogWarning($"Client {clientEndpoint} is already in the clients list, skipping.");
            }

            yield return null;
        }
    }



    private void SendHostStateToClient(IPEndPoint clientEndpoint)
    {
        var hostState = new PlayerState(playerController.transform.position, playerController.GetComponent<Blob>().Size);
        var stateJson = JsonUtility.ToJson(hostState);
        var bytes = Encoding.UTF8.GetBytes(stateJson);

        // Send the host's state to the newly connected client
        udpClient.SendAsync(bytes, bytes.Length, clientEndpoint);
        Debug.Log($"Sent host state to {clientEndpoint}: {stateJson}");
    }

    private IEnumerator Co_LaunchGame()
    {
        yield return SceneManager.LoadSceneAsync("Game");
        playerController = SpawnPlayer();
        finishedLoading = true;
        udpReady = true; // Ensure UDP is ready after loading the scene
    }

    public static void JoinGame(string hostName)
    {
        var session = CreateNew();
        session.isServer = false;

        try
        {
            session.udpClient = new UdpClient();
            session.serverEndpointUDP = GetIPEndPoint(hostName, UDPPortNumber);
            Debug.Log("UDP client initialized");

            session.tcpClient = new TcpClient();
            session.serverEndpointTCP = GetIPEndPoint(hostName, TcpPortNumber);
            Debug.Log("TCP client initialized, server endpoint: " + session.serverEndpointTCP);
        }
        catch (Exception ex)
        {
            Debug.LogError("Error initializing client: " + ex.Message);
        }

        session.StartCoroutine(session.Co_ConnectToServer());
        session.StartCoroutine(session.Co_LaunchGame());
    }

    private IEnumerator Co_ConnectToServer()
    {
        try
        {
            Debug.Log("Attempting to connect to server at " + serverEndpointTCP);
            tcpClient.Connect(serverEndpointTCP);
            Debug.Log("Connected to server via TCP!");
            udpReady = true; // Mark UDP as ready after successful TCP connection
        }
        catch (Exception ex)
        {
            Debug.LogError("Error connecting to server: " + ex.Message);
        }
        yield return null;
    }

    private static IPEndPoint GetIPEndPoint(string hostName, int port)
    {
        var addresses = Dns.GetHostAddresses(hostName);
        var ip = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
        return new IPEndPoint(ip, port);
    }

    private static GameSession CreateNew()
    {
        var go = new GameObject("GameSession");
        DontDestroyOnLoad(go);
        return go.AddComponent<GameSession>();
    }

    private static PlayerController SpawnPlayer()
    {
        var prefab = Resources.Load<PlayerController>("Player");
        Debug.Log("Player Spawned");
        return Instantiate(prefab);
    }

    private static OpponentController SpawnOpponent()
    {
        var prefab = Resources.Load<OpponentController>("Opponent");
        Debug.Log("Opponent Spawned");
        return Instantiate(prefab);
    }
    
    [Serializable]
    private class PlayerState
    {
        public Vector3 position;
        public float size;

        public PlayerState(Vector3 pos, float sz)
        {
            position = pos;
            size = sz;
        }
    }
}

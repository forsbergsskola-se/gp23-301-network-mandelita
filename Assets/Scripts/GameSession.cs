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
        
        if (!isServer) // Client
        {
            await SendPositionToServer();
            await ReceiveOpponentUpdates();
        }
        else // Server
        {
            await ReceivePositions();
        }
    }

    //Client
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

    // Client
    private async Task ReceiveOpponentUpdates()
    {
        try
        {
            var receiveResult = await udpClient.ReceiveAsync();
            var receivedJson = Encoding.UTF8.GetString(receiveResult.Buffer);

            if (!IsValidJson(receivedJson))
            {
                Debug.LogWarning("Invalid JSON format received, skipping packet.");
                return;
            }

            var opponentState = JsonUtility.FromJson<PlayerState>(receivedJson);
            if (opponentState == null)
            {
                Debug.LogWarning("Parsed opponentState is null, skipping update.");
                return;
            }

            Debug.Log("Client received opponent update");
            EnsureOpponentClient(receiveResult.RemoteEndPoint, opponentState.position, opponentState.size);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error receiving UDP packets: {ex.Message}");
        }
    }

    private void EnsureOpponentClient(IPEndPoint opponentEndpoint, Vector3 position, float size)
    {
        // Check if the opponent already exists; if not, spawn a new one
        if (!opponents.TryGetValue(opponentEndpoint, out var opponentController))
        {
            Debug.Log($"Spawning new opponent for {opponentEndpoint}");
            opponentController = SpawnOpponent();

            if (opponentController == null)
            {
                Debug.LogError($"Failed to spawn opponent for {opponentEndpoint}");
                return;
            }

            // Set the opponent's initial position and size
            //opponentController.transform.position = position;
            //opponentController.GetComponent<Blob>().Size = size;
            opponents[opponentEndpoint] = opponentController;
        }
        else
        {
            Debug.Log($"Updating opponent for {opponentEndpoint}");
            opponentController.UpdatePosition(position, size);
        }
    }
    

   
    // Server
    private async Task ReceivePositions()
    {
        Debug.Log("Server listening for positions...");

        while (true)
        {
            try
            {
                var receiveResult = await udpClient.ReceiveAsync();
                var fromEndpoint = receiveResult.RemoteEndPoint;

                if (fromEndpoint.Equals(serverEndpointUDP))
                    continue;

                var receivedJson = Encoding.UTF8.GetString(receiveResult.Buffer);
                Debug.Log($"Received position from {fromEndpoint}");

                if (!IsValidJson(receivedJson))
                {
                    Debug.LogWarning("Invalid JSON format received, skipping packet.");
                    continue;
                }

                var playerState = JsonUtility.FromJson<PlayerState>(receivedJson);
                if (playerState == null)
                {
                    Debug.LogWarning("Parsed playerState is null, skipping update.");
                    continue;
                }

                EnsureOpponentServer(fromEndpoint, playerState.position, playerState.size);
                BroadcastOpponentStates();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error receiving UDP packets: {ex.Message}");
            }

            await Task.Yield(); // Yield back to the main loop
        }
    }

    private void EnsureOpponentServer(IPEndPoint opponentEndpoint, Vector3 position, float size)
    {
        if (!finishedLoading)
        {
            Debug.Log("Game is not finished loading. Opponent will not be spawned yet.");
            return;
        }

        if (opponentEndpoint.Equals(serverEndpointUDP))
        {
            Debug.Log("Skipping spawn for host’s own opponentEndpoint.");
            return;
        }

        if (!opponents.TryGetValue(opponentEndpoint, out var opponentController))
        {
            Debug.Log($"Spawning new opponent for {opponentEndpoint}");
            opponentController = SpawnOpponent();

            if (opponentController == null)
            {
                Debug.LogError($"Failed to spawn opponent for {opponentEndpoint}");
                return;
            }

            // Set the opponent's initial position and size
            //opponentController.transform.position = position;
            //opponentController.GetComponent<Blob>().Size = size;
            opponents[opponentEndpoint] = opponentController;
        }
        else if (opponentController != null)
        {
            opponentController.UpdatePosition(position, size);
        }
    }

    // Server
    private void BroadcastOpponentStates()
    {
        foreach (var opponent in opponents)
        {
            if (opponent.Value == null) continue; // Ensure the opponent is still valid

            var state = new PlayerState(opponent.Value.transform.position, opponent.Value.GetComponent<Blob>().Size);
            var stateJson = JsonUtility.ToJson(state);
            var bytes = Encoding.UTF8.GetBytes(stateJson);

            Debug.Log("Broadcasting opponent state to all clients");

            // Send opponent state to all clients
            foreach (var clientEndpoint in clients) // Send to all connected clients
            {
                if (clientEndpoint != opponent.Key) // Don't send to the sender
                {
                    udpClient.SendAsync(bytes, bytes.Length, clientEndpoint);
                    Debug.Log($"Sent state to {clientEndpoint}");
                }
            }
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
            clients.Add(clientEndpoint);
            
            yield return null;
        }
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
    
        if (prefab == null)
        {
            Debug.LogError("Opponent prefab not found!");
            return null; // Return null if the prefab is not found
        }
    
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
}
using System;
using System.Collections;
using System.Collections.Generic;
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

    // Client
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

    private async Task ReceiveOpponentUpdates()
    {
        var receiveResult = await udpClient.ReceiveAsync();
        var receivedJson = Encoding.UTF8.GetString(receiveResult.Buffer);

        // Check if the message is a spawn message
        if (receivedJson.Contains("endpoint"))
        {
            var spawnMessage = JsonUtility.FromJson<OpponentSpawnMessage>(receivedJson);
            if (spawnMessage == null) return;

            // Spawn the opponent on the client side
            SpawnOpponentLocally(spawnMessage.endpoint, spawnMessage.position, spawnMessage.size);
            return;
        }

        // Handle opponent state update
        var opponentState = JsonUtility.FromJson<PlayerState>(receivedJson);
        if (opponentState == null) return;

        EnsureOpponentAndUpdatePosition(receiveResult.RemoteEndPoint, opponentState.position, opponentState.size);
    }

    private void SpawnOpponentLocally(IPEndPoint opponentEndpoint, Vector3 position, float size)
    {
        // Check if the opponent already exists
        if (!opponents.ContainsKey(opponentEndpoint))
        {
            var opponentController = SpawnOpponent();
            opponentController.UpdatePosition(position,size);
            opponents[opponentEndpoint] = opponentController; // Add to the dictionary
        }
        else
        {
            Debug.Log($"Opponent for {opponentEndpoint} already exists, not spawning again.");
        }
    }

    // Client
    private void EnsureOpponentAndUpdatePosition(IPEndPoint opponentEndpoint, Vector3 position, float size)
    {
        // Only spawn opponent if the game has finished loading
        if (!finishedLoading)
        {
            Debug.Log("Game is not finished loading. Opponent will not be spawned yet.");
            return;
        }

        // Check if the opponent already exists; if not, spawn a new one
        if (!opponents.TryGetValue(opponentEndpoint, out var opponentController))
        {
            Debug.Log($"Spawning new opponent for {opponentEndpoint}");

            // Skip spawning for the host's own opponentEndpoint to avoid duplicates
            if (opponentEndpoint.Equals(serverEndpointUDP))
            {
                Debug.Log("Skipping spawn for host’s own opponentEndpoint.");
                return;
            }

            opponentController = SpawnOpponent();
            opponents[opponentEndpoint] = opponentController;
        }

        // Update the opponent's position and size only if it's still valid
        if (opponentController != null && opponentController.gameObject != null)
        {
            Debug.Log($"Updating opponent position for {opponentEndpoint}");
            opponentController.UpdatePosition(position, size);
        }
        else
        {
            Debug.LogWarning($"Opponent controller for {opponentEndpoint} was null or destroyed.");
            opponents.Remove(opponentEndpoint); // Remove destroyed or invalid opponents
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
                {
                    Debug.Log("Skipping own packet.");
                    continue;
                }

                var receivedJson = Encoding.UTF8.GetString(receiveResult.Buffer);
                Debug.Log($"Raw JSON received: {receivedJson}");
                Debug.Log($"Received position from {fromEndpoint}");

                // Validate JSON format before deserialization
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

                EnsureOpponentAndUpdatePosition(fromEndpoint, playerState.position, playerState.size);
                BroadcastOpponentStates(); // Broadcasting opponent states after processing updates
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error receiving UDP packets: {ex.Message}");
            }

            await Task.Yield(); // Yield back to the main loop
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

            foreach (var client in clients)
            {
                if (client != null)
                {
                    udpClient.SendAsync(bytes, bytes.Length, client);
                    Debug.Log($"Sent opponent state to {client}");
                }
            }
        }
    }

    // Server 
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

    // Server
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

            // Notify all clients to spawn a new opponent for the newly connected client
            SpawnOpponentAndNotifyClients(clientEndpoint);

            yield return null;
        }
    }

    private void SpawnOpponentAndNotifyClients(IPEndPoint clientEndpoint)
    {
        // Spawn the opponent
        var opponentController = SpawnOpponent();

        // Create and send the spawn message to all clients
        var spawnMessage = new OpponentSpawnMessage(clientEndpoint, opponentController.transform.position, opponentController.GetComponent<Blob>().Size);
        var spawnMessageJson = JsonUtility.ToJson(spawnMessage);
        var bytes = Encoding.UTF8.GetBytes(spawnMessageJson);

        Debug.Log($"Notifying clients of new opponent spawn for {clientEndpoint}");

        // Send the spawn message to all clients
        foreach (var client in clients)
        {
            udpClient.SendAsync(bytes, bytes.Length, client);
        }
    }

    // Client
    public static void JoinGame(string hostName)
    {
        var session = CreateNew();
        session.isServer = false;

        try
        {
            session.udpClient = new UdpClient();
            session.serverEndpointUDP = new IPEndPoint(Dns.GetHostAddresses(hostName)[0], UDPPortNumber);

            Debug.Log("UDP client initialized for " + hostName);

            session.StartCoroutine(session.Co_ConnectToServer());

            Debug.Log("JoinGame successfully executed");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error in JoinGame: " + ex.Message);
        }
    }

    private IEnumerator Co_ConnectToServer()
{
    // Send initial connection request
    var request = new ConnectionRequestMessage();
    var requestJson = JsonUtility.ToJson(request);
    var requestBytes = Encoding.UTF8.GetBytes(requestJson);

    Debug.Log("Sending connection request to server...");

    using (var tcpClient = new TcpClient())
    {
        // Connect to server asynchronously
        var connectTask = tcpClient.ConnectAsync(serverEndpointTCP.Address, TcpPortNumber);
        
        // Yield until the connection task completes
        yield return new WaitUntil(() => connectTask.IsCompleted);

        if (connectTask.Exception != null)
        {
            Debug.LogError($"Failed to connect: {connectTask.Exception}");
            yield break; // Exit if there's an error
        }

        var stream = tcpClient.GetStream();

        // Send the connection request asynchronously
        var writeTask = stream.WriteAsync(requestBytes, 0, requestBytes.Length);

        // Yield until the write task completes
        yield return new WaitUntil(() => writeTask.IsCompleted);

        if (writeTask.Exception != null)
        {
            Debug.LogError($"Failed to send request: {writeTask.Exception}");
            yield break; // Exit if there's an error
        }

        Debug.Log("Connection request sent. Waiting for server response...");

        // Receive the host state
        var buffer = new byte[1024];
        var readTask = stream.ReadAsync(buffer, 0, buffer.Length);

        // Yield until the read task completes
        yield return new WaitUntil(() => readTask.IsCompleted);

        if (readTask.Exception != null)
        {
            Debug.LogError($"Failed to read response: {readTask.Exception}");
            yield break; // Exit if there's an error
        }

        // Get the number of bytes read
        int bytesRead = readTask.Result; 
        string jsonResponse = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        var hostState = JsonUtility.FromJson<PlayerState>(jsonResponse);
        if (hostState != null)
        {
            Debug.Log("Received host state from server, updating local state...");
            playerController.transform.position = hostState.position;
        }
        else
        {
            Debug.LogWarning("Received invalid host state from server.");
        }
    }

    udpReady = true;
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
    private IEnumerator Co_LaunchGame()
    {
        yield return SceneManager.LoadSceneAsync("Game");
        playerController = SpawnPlayer();
        finishedLoading = true;
        udpReady = true; // Ensure UDP is ready after loading the scene
    }

    private static bool IsValidJson(string json)
    {
        // A simple check for valid JSON, can be improved
        return !string.IsNullOrEmpty(json) && json.Trim().StartsWith("{") && json.Trim().EndsWith("}");
    }
}

[Serializable]
public class PlayerState
{
    public Vector3 position;
    public float size;

    public PlayerState(Vector3 position, float size)
    {
        this.position = position;
        this.size = size;
    }
}

[Serializable]
public class OpponentSpawnMessage
{
    public IPEndPoint endpoint;
    public Vector3 position;
    public float size;

    public OpponentSpawnMessage(IPEndPoint endpoint, Vector3 position, float size)
    {
        this.endpoint = endpoint;
        this.position = position;
        this.size = size;
    }
}

[Serializable]
public class ConnectionRequestMessage
{
    // Any necessary fields for connection requests can be added here
}

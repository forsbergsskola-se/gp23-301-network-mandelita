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
    private const int UDPPortNumber = 44445;
    private const int TcpPortNumber = 44446;
    private bool finishedLoading;
    private PlayerController playerController;
    public bool isServer;

    #region ------ Client -------
    private IPEndPoint serverEndpoint;
    private UdpClient udpClient;
    private TcpClient tcpClient;
    #endregion

    #region ------ Server -------
    private Dictionary<IPEndPoint, OpponentController> opponents = new();
    private TcpListener tcpListener;
    #endregion
    
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

    private async void FixedUpdate()
    {
        if (!finishedLoading) return;

        await SendAndReceivePositions();
    }

    // Handles both sending and receiving player positions
    private async Task SendAndReceivePositions()
    {
        Debug.Log("Update:");
        if (isServer)
        {
            Debug.Log("Server");
            await ReceivePositions(); // Server receives positions from clients
            await BroadcastPlayerStates(); // Server then broadcasts all positions to all clients
        }
        else
        {
            Debug.Log("Client");
            await SendPositionToServer(); // Clients send their position to the server
            await ReceivePositions(); // Clients receive opponent positions from the server
        }
    }
    
    private async Task ReceivePositions()
    {
        try
        {
            while (true) // Run continuously to check for packets
            {
                var receiveResult = await udpClient.ReceiveAsync();
                var fromEndpoint = receiveResult.RemoteEndPoint;
                var receivedData = Encoding.UTF8.GetString(receiveResult.Buffer);

                if (string.IsNullOrEmpty(receivedData))
                {
                    Debug.LogWarning("Received empty data from: " + fromEndpoint);
                    continue; // Skip empty data
                }

                var state = JsonUtility.FromJson<PlayerState>(receivedData);
                Debug.Log($"Received data from: {fromEndpoint} - Position: {state.position}, Size: {state.size}");

                EnsurePlayerAndUpdatePosition(fromEndpoint, state.position, state.size);
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
        {
            await Task.Delay(100); // Short delay before next check
        }
        catch (Exception ex)
        {
            Debug.LogError("Error receiving positions: " + ex.Message);
        }
    }


    
    private void EnsurePlayerAndUpdatePosition(IPEndPoint playerEndpoint, Vector3 playerPosition, float playerSize)
    {
        if (!opponents.TryGetValue(playerEndpoint, out var playerController))
        {
            playerController = SpawnOpponent(); // Spawn opponent if not found
            opponents[playerEndpoint] = playerController;
        }

        playerController.transform.position = playerPosition;
        playerController.GetComponent<Blob>().Size = playerSize;  
        Debug.Log("Update positions");
    }

    // Broadcasts the server's and players' states to all clients
    private async Task BroadcastPlayerStates()
    {
        var hostState = new PlayerState(playerController.transform.position, playerController.GetComponent<Blob>().Size);
        var hostJson = JsonUtility.ToJson(hostState);
        var hostBytes = Encoding.UTF8.GetBytes(hostJson);

        foreach (var endpoint in opponents.Keys)
        {
            await udpClient.SendAsync(hostBytes, hostBytes.Length, endpoint); // Send host state to all clients
        }

        foreach (var opponent in opponents)
        {
            var state = new PlayerState(opponent.Value.transform.position, opponent.Value.GetComponent<Blob>().Size);
            var json = JsonUtility.ToJson(state);
            var bytes = Encoding.UTF8.GetBytes(json);
        
            foreach (var endpoint in opponents.Keys)
            {
                await udpClient.SendAsync(bytes, bytes.Length, endpoint); // Broadcast to all clients
                Debug.Log("Broadcast");
            }
        }
    }

    // Sends the player's position to the server
    private async Task SendPositionToServer()
    {
        if (serverEndpoint == null)
        {
            Debug.LogError("Server endpoint is not set.");
            return;
        }
        try
        {
            var position = playerController.transform.position;
            var size = playerController.GetComponent<Blob>().Size;

            var state = new PlayerState(position, size);
            var json = JsonUtility.ToJson(state);
            var bytes = Encoding.UTF8.GetBytes(json);
            await udpClient.SendAsync(bytes, bytes.Length, serverEndpoint);
            Debug.Log("Client sent position...");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error sending position to server: " + ex.Message);
        }
    }

    // Host game setup
    public static void HostGame()
    {
        try
        {
            var session = CreateNew();  // Creates the GameSession object
            session.isServer = true;

            session.udpClient = new UdpClient(UDPPortNumber);  // Initialize UDP listener
            Debug.Log("UDP Listener started on port " + UDPPortNumber);

            session.tcpListener = new TcpListener(IPAddress.Any, TcpPortNumber);  // Initialize TCP listener
            session.tcpListener.Start();  // Start listening for TCP clients
            Debug.Log("TCP Listener started on port " + TcpPortNumber);

            session.StartCoroutine(session.Co_AcceptClients());  // Accept clients via TCP
            session.StartCoroutine(session.Co_LaunchGame());     // Launch the game scene
            
            Debug.Log("HostGame successfully started");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error in HostGame: " + ex.Message);
        }
    }

    // Accepts clients via TCP
    private IEnumerator Co_AcceptClients()
    {
        Debug.Log("Waiting for TCP clients to connect...");
    
        var task = tcpListener.AcceptTcpClientAsync();  // Non-blocking
        while (!task.IsCompleted)
        {
            yield return null;  // Wait for connection
        }

        var client = task.Result;
        Debug.Log("Client connected via TCP!"); 

        var clientEndpoint = (IPEndPoint)client.Client.RemoteEndPoint;
        var opponentController = SpawnOpponent();
        opponents[clientEndpoint] = opponentController;
        
        yield return null;  
    }

    // Launches the game scene
    private IEnumerator Co_LaunchGame()
    {
        yield return SceneManager.LoadSceneAsync("Game");
        playerController = SpawnPlayer();
        finishedLoading = true;
    }
    
    // Client joins game and connects to server
    public static void JoinGame(string hostName)
    {
        var session = CreateNew();
        session.isServer = false;

        try
        {
            session.udpClient = new UdpClient(UDPPortNumber);  
            Debug.Log("UDP client initialized"); 

            session.tcpClient = new TcpClient();
            session.serverEndpoint = GetIPEndPoint(hostName, TcpPortNumber);
            Debug.Log("TCP client initialized, server endpoint: " + session.serverEndpoint);
        }
        catch (Exception ex)
        {
            Debug.LogError("Error initializing client: " + ex.Message);
        }

        session.StartCoroutine(session.Co_ConnectToServer());  
        session.StartCoroutine(session.Co_LaunchGame());
    }

    // Connects client to server
    private IEnumerator Co_ConnectToServer()
    {
        try
        {
            Debug.Log("Attempting to connect to server at " + serverEndpoint);
            tcpClient.Connect(serverEndpoint);  
            Debug.Log("Connected to server via TCP!");
        }
        catch (Exception ex)
        {
            Debug.LogError("Error connecting to server: " + ex.Message);
        }

        yield return null;
    }

    // Resolves IP address from hostname
    private static IPEndPoint GetIPEndPoint(string hostName, int port)
    {
        try
        {
            var address = Dns.GetHostAddresses(hostName).First();
            Debug.Log("Resolved host address for " + hostName); 
            return new IPEndPoint(address, port);
        }
        catch (Exception ex)
        {
            Debug.LogError("Error resolving IP address for " + hostName + ": " + ex.Message);
            throw;
        }
    }
}

// Bundle for player position and size
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

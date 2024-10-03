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

    //TODO Figure out why the host can see that spawn client and eat it, but doesnt update its position or size,
    //TODO and why the client does not see and opponent spawned when joining. The host should be there.. 
    
    private async void FixedUpdate()
    {
        if (!finishedLoading) return;

        await SendAndReceivePositions();
    }

    private async Task SendAndReceivePositions()
    {
        if (isServer)
        {
            await ReceivePositions(); // Server receives positions from clients
            await BroadcastOpponentStates(); // Server then broadcasts opponent positions to all clients
        }
        else
        {
            await SendPositionToServer(); // Clients send their position to the server
            await ReceivePositions(); // Clients receive opponent positions from the server
        }
    }
    
    private async Task ReceivePositions()
    {
        try
        {
            while (udpClient.Available > 0)
            {
                var receiveResult = await udpClient.ReceiveAsync();
                var fromEndpoint = receiveResult.RemoteEndPoint;
                var bytes = receiveResult.Buffer;
                var receivedData = Encoding.UTF8.GetString(bytes);

                var state = JsonUtility.FromJson<PlayerState>(receivedData);  

                // Check if the message came from the server (host) and ensure opponent representation
                if (fromEndpoint.Equals(serverEndpoint))
                {
                    EnsureOpponentAndUpdatePosition(fromEndpoint, state.position, state.size); // Host update
                    Debug.Log("Host Update");
                }
                else
                {
                    // Update opponent's position and size
                    EnsureOpponentAndUpdatePosition(fromEndpoint, state.position, state.size); 
                    Debug.Log("Client Update");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Error receiving positions: " + ex.Message);
        }
    }

    private void EnsureOpponentAndUpdatePosition(IPEndPoint opponentEndpoint, Vector3 opponentPosition, float opponentSize)
    {
        if (!opponents.TryGetValue(opponentEndpoint, out var opponentController))
        {
            opponentController = SpawnOpponent(); // Spawn opponent if not found
            opponents[opponentEndpoint] = opponentController;
        }

        // Update position and size for both host and client opponents
        opponentController.transform.position = opponentPosition;
        opponentController.GetComponent<Blob>().Size = opponentSize;  
    }

    private async Task BroadcastOpponentStates()
    {
        // Include the server/host player state in the broadcast
        var hostState = new PlayerState(playerController.transform.position, playerController.GetComponent<Blob>().Size);
        var hostJson = JsonUtility.ToJson(hostState);
        var hostBytes = Encoding.UTF8.GetBytes(hostJson);

        foreach (var endpoint in opponents.Keys)
        {
            await udpClient.SendAsync(hostBytes, hostBytes.Length, endpoint); // Send host state to all clients
        }

        // Send all opponent states as well
        foreach (var opponent in opponents)
        {
            var state = new PlayerState(opponent.Value.transform.position, opponent.Value.GetComponent<Blob>().Size);
            var json = JsonUtility.ToJson(state);
            var bytes = Encoding.UTF8.GetBytes(json);
        
            foreach (var endpoint in opponents.Keys)
            {
                await udpClient.SendAsync(bytes, bytes.Length, endpoint); // Broadcast to all clients
            }
        }
    }

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
            var json = JsonUtility.ToJson(state);  // Serialize player state
            var bytes = Encoding.UTF8.GetBytes(json);
            Debug.Log("Client sent position...");
            await udpClient.SendAsync(bytes, bytes.Length, serverEndpoint);
        }
        catch (Exception ex)
        {
            Debug.LogError("Error sending position to server: " + ex.Message);
        }
    }

    
    // Host Game and listen for connections
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

            session.StartCoroutine(session.Co_AcceptClients());  // Coroutine to accept clients via TCP
            session.StartCoroutine(session.Co_LaunchGame());     // Launch the game scene

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
    
        // Use async method to prevent blocking the main thread
        var task = tcpListener.AcceptTcpClientAsync();  // Non-blocking
        while (!task.IsCompleted)
        {
            yield return null;  // Yield until a client connects
        }

        var client = task.Result;  // Get the connected TCP client
        Debug.Log("Client connected via TCP!"); 

        var clientEndpoint = (IPEndPoint)client.Client.RemoteEndPoint;
        var opponentController = SpawnOpponent();  // Spawn a new opponent for the client
        opponents.TryAdd(clientEndpoint, opponentController);  // Add to opponents dictionary
    
        Debug.Log("Opponent spawned and added to the game!");
        
        yield return null;  
    }

    private IEnumerator Co_LaunchGame()
    {
        yield return SceneManager.LoadSceneAsync("Game");
        playerController = SpawnPlayer();
        finishedLoading = true;
    }
    
    
    // Join game as a client using TCP connection, connect to server and launch game:
    public static void JoinGame(string hostName)
    {
        var session = CreateNew();
        session.isServer = false;
        try
        {
            session.udpClient = new UdpClient(UDPPortNumber);  
            Debug.Log("UDP client initialized"); // Initialize UDP OK!

        }
        catch (Exception ex)
        {
            Debug.LogError("Error initializing UDP client: " + ex.Message);
        }
        try
        {
            session.tcpClient = new TcpClient();
            session.serverEndpoint = GetIPEndPoint(hostName, TcpPortNumber);
            Debug.Log("TCP client initialized, server endpoint: " + session.serverEndpoint); // Initialize TCP OK!
        }
        catch (Exception ex)
        {
            Debug.LogError("Error initializing TCP client or resolving server endpoint: " + ex.Message);
        }
        session.StartCoroutine(session.Co_ConnectToServer());  
        session.StartCoroutine(session.Co_LaunchGame());

    }
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
    private static IPEndPoint GetIPEndPoint(string hostName, int port)
    {
        try
        {
            var address = Dns.GetHostAddresses(hostName).First();
            Debug.Log("Resolved host Address for " + hostName); 
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
